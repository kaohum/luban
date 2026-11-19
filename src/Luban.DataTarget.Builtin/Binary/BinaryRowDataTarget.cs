// Copyright 2025 Code Philosophy
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE SOFTWARE'S OTHER DEALINGS IN THE
// SOFTWARE.

using System.Security.Cryptography;
using Luban.Datas;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;


using System.Security.Cryptography;
using Luban.Datas;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Binary;

/// <summary>
/// 二进制行表导出 v3（列压缩：字节粒度收窄 + 调色板 + u16 字符串索引 + u24/u24 变长指针）。
/// 布局：[magic "TBIX" 4B][BE i32 headerLen][BE i32 restLen]
/// [header: u8 fmtVer=3 + signature(varint+utf8) + 字符串表(同紧凑编码) + n(varint) + planCount(varint) + plan 段]
///   plan 条目：kind u8 + name(string) [+ Narrow: width u8 + min i64 LE] [+ Palette: idxWidth u8 + count varint + count*i64 LE]
/// [fixed region: n * stride'（plan 决定各列宽度），导出原序，packed 无对齐，多字节小端]
///     kind Raw=原宽 LE；Narrow=width 字节 LE（值-min 无符号）；Palette=idxWidth 字节 LE 索引；
///     StrU16=2B 字符串表索引；Blob6=3B off + 3B len -> shared pool；nullable 槽 = 1B flag + 载荷（null 时载荷占位 0）
/// [shared pool: 去重后的变长 blob（紧凑编码），内容寻址]
/// 对齐要求：cs-bin 模板按同一 plan 解码（运行期读 plan 表，字段按名匹配），两侧规则必须逐字节一致。
/// </summary>
[DataTarget("bin-row")]
public class BinaryRowDataTarget : DataTargetBase
{
    protected override string DefaultOutputFileExt => "bytes";

    private static readonly byte[] Magic = { (byte)'T', (byte)'B', (byte)'I', (byte)'X' };

    private const byte FormatVersion = 3;
    private const byte KindRaw = 0;
    private const byte KindNarrow = 1;
    private const byte KindPalette = 2;
    private const byte KindStrU16 = 3;
    private const byte KindBlob6 = 5;

    private const int MaxPoolBytes = 0xFFFFFF;

    private sealed class FieldPlan
    {
        public byte Kind;
        public string Name;
        public byte Width;      // Narrow: FOR 宽度；Palette: 索引宽度
        public long Min;        // Narrow: 基准最小值
        public long[] Palette;  // Palette: 值表
        public byte PaletteValWidth; // Palette: 值表元素宽度（按值域最小化）
        public Dictionary<long, int> PaletteIndex; // 装配期索引
        public bool IsStringIndex; // TString 且数据为 DString（字符串表索引）——区别于 text-id(int)
        public bool Nullable;
    }

    public override OutputFile ExportTable(DefTable table, List<Record> records)
    {
        try
        {
            return ExportTableCore(table, records);
        }
        catch (Exception e)
        {
            throw new Exception($"[bin-row] table={table.Name} valueBean={table.ValueTType.DefBean.Name} abstract={table.ValueTType.DefBean.IsAbstractType}: {e.Message}", e);
        }
    }

    private OutputFile ExportTableCore(DefTable table, List<Record> records)
    {
        var bean = table.ValueTType.DefBean;
        if (bean.IsAbstractType)
        {
            throw new Exception($"[bin-row] 多态值 bean 不支持: {table.Name}（定长行前提）");
        }

        var builder = new StringTableBuilder();
        var visitor = new BinaryDataVisitor { StringTable = builder };

        // 1. 注册全表字符串（含嵌套）
        var tmp = new ByteBuf(1 << 10);
        foreach (var d in records)
        {
            d.Data.Apply(visitor, tmp);
        }

        // 2. 列分析 → plan
        var defFields = bean.HierarchyFields;
        var plans = BuildPlans(table, records, defFields, builder);

        // 3. 行装配
        int stride = 0;
        int planIdx = 0;
        foreach (var f in defFields)
        {
            if (!f.NeedExport()) continue;
            var p = plans[planIdx++];
            stride += (f.CType.IsNullable ? 1 : 0) + PayloadWidthOf(f, p);
        }

        var fixedBuf = new ByteBuf(Math.Max(16, records.Count * stride + 16));
        var poolBuf = new ByteBuf(1 << 12);
        var poolIndex = new Dictionary<string, (int off, int len)>();
        var scratch = new ByteBuf(1 << 10);

        foreach (var d in records)
        {
            var dbean = (DBean)d.Data;
            if (dbean.Fields.Count != defFields.Count)
            {
                throw new Exception($"[bin-row] field-count mismatch: {table.Name} data.Fields={dbean.Fields.Count} defFields={defFields.Count} impl={dbean.Type.Name} declared={bean.Name}");
            }
            int index = 0;
            int pi = 0;
            foreach (var field in dbean.Fields)
            {
                var defField = (DefField)defFields[index++];
                if (!defField.NeedExport()) continue;
                WriteSlot(table, defField, field, plans[pi++], visitor, scratch, fixedBuf, poolBuf, poolIndex, builder);
            }
        }

        if (fixedBuf.Size != (long)records.Count * stride)
        {
            throw new Exception($"[bin-row] 行装配错位: {table.Name} fixed={fixedBuf.Size} expect={records.Count * stride}");
        }
        if (poolBuf.Size > MaxPoolBytes)
        {
            throw new Exception($"[bin-row] 共享池超 u24: {table.Name} pool={poolBuf.Size}");
        }

        // 4. header（版本 + 签名 + 字符串表 + n + plan 段）
        var planBuf = BuildPlanBytes(plans);
        var headerBuf = new ByteBuf(1 << 10);
        headerBuf.WriteByte(FormatVersion);
        headerBuf.WriteString(table.SignatureId);
        builder.Write(headerBuf);
        headerBuf.WriteSize(records.Count);
        headerBuf.WriteBytesWithoutSize(planBuf);

        // 4.5 plan sidecar（供 cs-bin 二阶段代码生成特化常量；未配置 planDir 时跳过）
        WritePlanSidecar(table, planBuf);

        // 5. 组装
        var bytes = new ByteBuf((int)(12 + headerBuf.Size + fixedBuf.Size + poolBuf.Size));
        bytes.WriteBytesWithoutSize(Magic);
        WriteBE32(bytes, (int)headerBuf.Size);
        WriteBE32(bytes, (int)(fixedBuf.Size + poolBuf.Size));
        bytes.WriteBytesWithoutSize(headerBuf.CopyData());
        bytes.WriteBytesWithoutSize(fixedBuf.CopyData());
        bytes.WriteBytesWithoutSize(poolBuf.CopyData());
        return CreateOutputFile($"{table.OutputDataFile}.{OutputFileExt}", bytes.CopyData());
    }

    // ==================== 列分析 ====================

    private static FieldPlan[] BuildPlans(DefTable table, List<Record> records, List<DefField> defFields, StringTableBuilder builder)
    {
        var exported = new List<DefField>();
        var exportedIdx = new List<int>();
        for (int i = 0; i < defFields.Count; i++)
        {
            if (defFields[i].NeedExport()) { exported.Add(defFields[i]); exportedIdx.Add(i); }
        }
        var plans = new FieldPlan[exported.Count];

        for (int i = 0; i < exported.Count; i++)
        {
            var f = exported[i];
            var plan = new FieldPlan { Kind = KindRaw, Name = f.Name, Width = (byte)SlotWidth(f.CType), Nullable = f.CType.IsNullable };
            plans[i] = plan;

            if (f.CType is TString)
            {
                // 区分字符串表索引字段与多语言 text-id（数据 DInt、生成侧 int）
                bool isStringIndex = false;
                foreach (var r in records)
                {
                    var dbean = (DBean)r.Data;
                    var v = dbean.Fields[exportedIdx[i]];
                    if (v == null) continue;
                    isStringIndex = v is DString;
                    break;
                }
                plan.IsStringIndex = isStringIndex;
                if (isStringIndex && builder.Count <= 0xFFFF)
                {
                    plan.Kind = KindStrU16;
                    plan.Width = 2;
                }
                continue;
            }

            if (IsLongFamily(f.CType))
            {
                AnalyzeIntegerColumn(f, records, exportedIdx[i], plan);
                continue;
            }

            if (IsVariableField(f.CType))
            {
                plan.Kind = KindBlob6;
                plan.Width = 6;
            }
            // bool/byte/float/double 保持 Raw
        }
        return plans;
    }

    private static bool IsVariableField(TType t)
    {
        return !(t is TBool) && !(t is TByte) && !(t is TShort) && !(t is TInt)
            && !(t is TLong) && !(t is TEnum) && !(t is TFloat) && !(t is TDouble)
            && !(t is TDateTime) && !(t is TDay) && !(t is THour) && !(t is TMinute)
            && !(t is TSecond) && !(t is TMillisecond) && !(t is TString);
    }

    private static void AnalyzeIntegerColumn(DefField f, List<Record> records, int fieldIdx, FieldPlan plan)
    {
        int origWidth = SlotWidth(f.CType);
        long min = long.MaxValue, max = long.MinValue;
        var distinct = new HashSet<long>();
        bool highCard = false;
        int count = 0;
        foreach (var r in records)
        {
            var dbean = (DBean)r.Data;
            if (fieldIdx >= dbean.Fields.Count) break;
            var v = dbean.Fields[fieldIdx];
            if (v == null) continue;
            if (!TryGetLong(v, out long lv)) continue;
            if (lv < min) min = lv;
            if (lv > max) max = lv;
            count++;
            if (!highCard)
            {
                distinct.Add(lv);
                if (distinct.Count > 65536) { highCard = true; distinct = null; }
            }
        }
        if (count == 0 || max < min)
        {
            return;
        }

        ulong range = unchecked((ulong)max - (ulong)min);
        int nw = range <= 0xFF ? 1 : range <= 0xFFFF ? 2 : range <= 0xFFFFFF ? 3 : range <= 0xFFFFFFFF ? 4 : origWidth;
        if (nw > origWidth) nw = origWidth;
        bool narrowOk = nw < origWidth;

        bool palOk = false;
        int iw = 0;
        if (!highCard && distinct != null)
        {
            int dc = distinct.Count;
            if (dc <= 255 && origWidth >= 2 && count >= dc * 2) { palOk = true; iw = 1; }
            else if (dc <= 65535 && origWidth >= 3 && count >= dc * 2) { palOk = true; iw = 2; }
        }

        if (palOk && (!narrowOk || iw <= nw))
        {
            plan.Kind = KindPalette;
            plan.Width = (byte)iw;
            plan.Palette = new long[distinct.Count];
            distinct.CopyTo(plan.Palette);
            long pmin = long.MaxValue, pmax = long.MinValue;
            foreach (var pv in plan.Palette) { if (pv < pmin) pmin = pv; if (pv > pmax) pmax = pv; }
            plan.PaletteValWidth = pmin >= sbyte.MinValue && pmax <= sbyte.MaxValue ? (byte)1
                : pmin >= short.MinValue && pmax <= short.MaxValue ? (byte)2
                : pmin >= int.MinValue && pmax <= int.MaxValue ? (byte)4 : (byte)8;
            plan.PaletteIndex = new Dictionary<long, int>(distinct.Count);
            for (int k = 0; k < plan.Palette.Length; k++) plan.PaletteIndex[plan.Palette[k]] = k;
            return;
        }
        if (narrowOk)
        {
            plan.Kind = KindNarrow;
            plan.Width = (byte)nw;
            plan.Min = min;
        }
    }

    private static bool IsLongFamily(TType t)
    {
        return t is TShort || t is TInt || t is TEnum || t is TLong || t is TDateTime
            || t is TDay || t is THour || t is TMinute || t is TSecond || t is TMillisecond;
    }

    private static bool TryGetLong(DType v, out long value)
    {
        switch (v)
        {
            case DByte x: value = x.Value; return true;
            case DShort x: value = x.Value; return true;
            case DInt x: value = x.Value; return true;
            case DEnum x: value = x.Value; return true;
            case DLong x: value = x.Value; return true;
            case DDateTime x: value = x.UnixTimeOfCurrentContext(); return true;
            case DDay x: value = x.Value; return true;
            case DHour x: value = x.Value; return true;
            case DMinute x: value = x.Value; return true;
            case DSecond x: value = x.Value; return true;
            case DMillisecond x: value = x.Value; return true;
            default: value = 0; return false;
        }
    }

    // ==================== 行装配 ====================

    private static int PayloadWidthOf(DefField defField, FieldPlan plan)
    {
        switch (plan.Kind)
        {
            case KindNarrow: return plan.Width;
            case KindPalette: return plan.Width;
            case KindStrU16: return 2;
            case KindBlob6: return 6;
            default: return SlotWidth(defField.CType);
        }
    }

    private void WriteSlot(DefTable table, DefField defField, DType field, FieldPlan plan, BinaryDataVisitor visitor, ByteBuf scratch,
        ByteBuf fixedBuf, ByteBuf poolBuf, Dictionary<string, (int off, int len)> poolIndex, StringTableBuilder builder)
    {
        bool nullable = defField.CType.IsNullable;
        if (nullable)
        {
            fixedBuf.WriteByte(field != null ? (byte)1 : (byte)0);
        }
        int payloadWidth = PayloadWidthOf(defField, plan);
        if (field == null)
        {
            for (int i = 0; i < payloadWidth; i++) fixedBuf.WriteByte(0);
            return;
        }

        switch (plan.Kind)
        {
            case KindNarrow:
            {
                if (!TryGetLong(field, out long lv))
                {
                    throw new Exception($"[bin-row] Narrow 列取值失败: {table.Name}.{defField.Name}");
                }
                ulong enc = unchecked((ulong)(lv - plan.Min));
                WriteLE(fixedBuf, enc, plan.Width);
                return;
            }
            case KindPalette:
            {
                TryGetLong(field, out long lv);
                if (!plan.PaletteIndex.TryGetValue(lv, out int pi))
                {
                    throw new Exception($"[bin-row] Palette 列索引失败: {table.Name}.{defField.Name} v={lv}");
                }
                WriteLE(fixedBuf, (ulong)pi, plan.Width);
                return;
            }
            case KindStrU16:
            {
                var ds = (DString)field;
                int si = builder.GetOrAddIndex(ds.Value);
                if (si > 0xFFFF)
                {
                    throw new Exception($"[bin-row] 字符串索引超 u16: {table.Name}.{defField.Name} idx={si}");
                }
                WriteLE16(fixedBuf, (short)(ushort)si);
                return;
            }
            case KindBlob6:
            {
                scratch.Clear();
                if (nullable) scratch.WriteBool(true);
                field.Apply(visitor, scratch);
                var blob = scratch.CopyData();
                string hash;
#if NET8_0_OR_GREATER
                hash = Convert.ToHexString(SHA1.HashData(blob));
#else
                using var sha = SHA1.Create();
                hash = Convert.ToHexString(sha.ComputeHash(blob));
#endif
                if (!poolIndex.TryGetValue(hash, out var slot))
                {
                    slot = ((int)poolBuf.Size, blob.Length);
                    poolBuf.WriteBytesWithoutSize(blob);
                    poolIndex[hash] = slot;
                }
                if (slot.off > MaxPoolBytes || slot.len > MaxPoolBytes)
                {
                    throw new Exception($"[bin-row] blob 超 u24: {table.Name}.{defField.Name} off={slot.off} len={slot.len}");
                }
                WriteLE(fixedBuf, (ulong)slot.off, 3);
                WriteLE(fixedBuf, (ulong)slot.len, 3);
                return;
            }
            default:
            {
                switch (defField.CType)
                {
                    case TBool: fixedBuf.WriteByte(((DBool)field).Value ? (byte)1 : (byte)0); break;
                    case TByte: fixedBuf.WriteByte(((DByte)field).Value); break;
                    case TShort: WriteLE16(fixedBuf, ((DShort)field).Value); break;
                    case TInt: WriteLE32(fixedBuf, ((DInt)field).Value); break;
                    case TEnum: WriteLE32(fixedBuf, ((DEnum)field).Value); break;
                    case TFloat: WriteLE32(fixedBuf, FloatBits(((DFloat)field).Value)); break;
                    case TLong: WriteLE64(fixedBuf, ((DLong)field).Value); break;
                    case TDouble: WriteLE64(fixedBuf, DoubleBits(((DDouble)field).Value)); break;
                    case TDateTime: WriteLE64(fixedBuf, ((DDateTime)field).UnixTimeOfCurrentContext()); break;
                    case TDay: WriteLE64(fixedBuf, ((DDay)field).Value); break;
                    case THour: WriteLE64(fixedBuf, ((DHour)field).Value); break;
                    case TMinute: WriteLE64(fixedBuf, ((DMinute)field).Value); break;
                    case TSecond: WriteLE64(fixedBuf, ((DSecond)field).Value); break;
                    case TMillisecond: WriteLE64(fixedBuf, ((DMillisecond)field).Value); break;
                    case TString:
                        if (field is DString ds2) WriteLE32(fixedBuf, builder.GetOrAddIndex(ds2.Value));
                        else WriteLE32(fixedBuf, ((DInt)field).Value);
                        break;
                    default:
                        throw new Exception($"[bin-row] Raw 分支不支持的类型: {table.Name}.{defField.Name} {defField.CType.GetType().Name}");
                }
                return;
            }
        }
    }

    /// <summary>plan 段序列化：planCount + 每列 kind/name/width/nullable [+ Narrow min] [+ Palette valWidth/count/values]。</summary>
    private static byte[] BuildPlanBytes(FieldPlan[] plans)
    {
        var planBuf = new ByteBuf(1 << 10);
        planBuf.WriteSize(plans.Length);
        foreach (var p in plans)
        {
            planBuf.WriteByte(p.Kind);
            planBuf.WriteString(p.Name);
            planBuf.WriteByte(p.Width);
            planBuf.WriteByte(p.Nullable ? (byte)1 : (byte)0);
            if (p.Kind == KindNarrow)
            {
                WriteLE64(planBuf, p.Min);
            }
            else if (p.Kind == KindPalette)
            {
                planBuf.WriteByte(p.PaletteValWidth);
                planBuf.WriteSize(p.Palette.Length);
                foreach (var v in p.Palette)
                {
                    ulong raw = unchecked((ulong)v);
                    for (int i = 0; i < p.PaletteValWidth; i++) planBuf.WriteByte((byte)(raw >> (8 * i)));
                }
            }
        }
        return planBuf.CopyData();
    }

    /// <summary>plan sidecar 落盘（{OutputDataFile}.plan，内容 = plan 段原始字节；cs-bin 生成期按名消费）。</summary>
    private void WritePlanSidecar(DefTable table, byte[] planBuf)
    {
        string planDir = EnvManager.Current.GetOptionOrDefault("bin-row", "planDir", true, "");
        if (string.IsNullOrWhiteSpace(planDir))
        {
            return;
        }
        System.IO.Directory.CreateDirectory(planDir);
        string path = System.IO.Path.Combine(planDir, $"{table.OutputDataFile}.plan");
        System.IO.File.WriteAllBytes(path, planBuf);
    }

    /// <summary>槽宽（不含 nullable flag；Raw 用；与 cs-bin 模板宽度表严格一致）。</summary>
    internal static int SlotWidth(TType t)
    {
        switch (t)
        {
            case TByte: return 1;
            case TBool: return 1;
            case TShort: return 2;
            case TInt: return 4;
            case TEnum: return 4;
            case TFloat: return 4;
            case TLong: return 8;
            case TDouble: return 8;
            case TDateTime: return 8;
            case TString: return 4;
            default: return 8;
        }
    }

    // ==================== 字节写 ====================

    private static void WriteLE(ByteBuf x, ulong v, int width)
    {
        for (int i = 0; i < width; i++) x.WriteByte((byte)(v >> (8 * i)));
    }

    private static void WriteBE32(ByteBuf x, int v)
    {
        x.WriteByte((byte)(v >> 24)); x.WriteByte((byte)(v >> 16)); x.WriteByte((byte)(v >> 8)); x.WriteByte((byte)v);
    }

    private static void WriteLE16(ByteBuf x, short v)
    {
        x.WriteByte((byte)v); x.WriteByte((byte)(v >> 8));
    }

    internal static void WriteLE32(ByteBuf x, int v)
    {
        x.WriteByte((byte)v); x.WriteByte((byte)(v >> 8)); x.WriteByte((byte)(v >> 16)); x.WriteByte((byte)(v >> 24));
    }

    internal static void WriteLE64(ByteBuf x, long v)
    {
        WriteLE32(x, (int)v); WriteLE32(x, (int)(v >> 32));
    }

    private static int FloatBits(float f) => BitConverter.SingleToInt32Bits(f);

    private static long DoubleBits(double d) => BitConverter.DoubleToInt64Bits(d);
}
