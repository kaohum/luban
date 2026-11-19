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
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

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
/// [header: u8 fmtVer=3 + signature(varint+utf8) + 字符串表 + n(varint) + plan 段]
/// [fixed region: n × stride'（plan 决定各列宽度），导出原序，packed 无对齐，多字节小端]
///     Raw=原宽 LE；Narrow=width 字节 LE（值-min 无符号）；Palette=索引宽度字节 LE；
///     StrU16=2B 字符串表索引；Blob6=3B off + 3B len → 共享池；nullable 槽首 1B flag
/// [shared pool: 去重后的变长 blob（紧凑编码），内容寻址]
/// 列编码方案由 BinRowPlanAnalyzer 统一分析（与 cs-bin 代码生成共享同一份进程内结果）。
/// </summary>
[DataTarget("bin-row")]
public class BinaryRowDataTarget : DataTargetBase
{
    protected override string DefaultOutputFileExt => "bytes";

    private static readonly byte[] Magic = { (byte)'T', (byte)'B', (byte)'I', (byte)'X' };

    private const byte FormatVersion = 3;
    private const int MaxPoolBytes = 0xFFFFFF;

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
        var tmp = new ByteBuf(1 << 10);
        foreach (var d in records)
        {
            d.Data.Apply(visitor, tmp);
        }

        var plan = BinRowPlanAnalyzer.Get(table, records);
        var fields = plan.Fields;

        var fixedBuf = new ByteBuf(Math.Max(16, records.Count * plan.Stride + 16));
        var poolBuf = new ByteBuf(1 << 12);
        var poolIndex = new Dictionary<string, (int off, int len)>();
        var scratch = new ByteBuf(1 << 10);

        var defFields = bean.HierarchyFields;
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
                WriteSlot(table, defField, field, fields[pi++], visitor, scratch, fixedBuf, poolBuf, poolIndex, builder);
            }
        }

        if (fixedBuf.Size != (long)records.Count * plan.Stride)
        {
            throw new Exception($"[bin-row] 行装配错位: {table.Name} fixed={fixedBuf.Size} expect={records.Count * plan.Stride}");
        }
        if (poolBuf.Size > MaxPoolBytes)
        {
            throw new Exception($"[bin-row] 共享池超 u24: {table.Name} pool={poolBuf.Size}");
        }

        var headerBuf = new ByteBuf(1 << 10);
        headerBuf.WriteByte(FormatVersion);
        headerBuf.WriteString(table.SignatureId);
        builder.Write(headerBuf);
        headerBuf.WriteSize(records.Count);
        headerBuf.WriteBytesWithoutSize(plan.PlanBytes);

        var bytes = new ByteBuf((int)(12 + headerBuf.Size + fixedBuf.Size + poolBuf.Size));
        bytes.WriteBytesWithoutSize(Magic);
        WriteBE32(bytes, (int)headerBuf.Size);
        WriteBE32(bytes, (int)(fixedBuf.Size + poolBuf.Size));
        bytes.WriteBytesWithoutSize(headerBuf.CopyData());
        bytes.WriteBytesWithoutSize(fixedBuf.CopyData());
        bytes.WriteBytesWithoutSize(poolBuf.CopyData());
        return CreateOutputFile($"{table.OutputDataFile}.{OutputFileExt}", bytes.CopyData());
    }

    private void WriteSlot(DefTable table, DefField defField, DType field, BinRowPlanAnalyzer.FieldPlanEntry plan, BinaryDataVisitor visitor, ByteBuf scratch,
        ByteBuf fixedBuf, ByteBuf poolBuf, Dictionary<string, (int off, int len)> poolIndex, StringTableBuilder builder)
    {
        bool nullable = defField.CType.IsNullable;
        if (nullable)
        {
            fixedBuf.WriteByte(field != null ? (byte)1 : (byte)0);
        }
        if (field == null)
        {
            for (int i = 0; i < plan.Width; i++) fixedBuf.WriteByte(0);
            return;
        }

        switch (plan.Kind)
        {
            case BinRowPlanAnalyzer.KindNarrow:
            {
                if (!BinRowPlanAnalyzer.TryGetLong(field, out long lv))
                {
                    throw new Exception($"[bin-row] Narrow 列取值失败: {table.Name}.{defField.Name}");
                }
                ulong enc = unchecked((ulong)(lv - plan.Min));
                WriteLE(fixedBuf, enc, plan.Width);
                return;
            }
            case BinRowPlanAnalyzer.KindPalette:
            {
                BinRowPlanAnalyzer.TryGetLong(field, out long lv);
                if (!plan.PaletteIndex.TryGetValue(lv, out int pi))
                {
                    throw new Exception($"[bin-row] Palette 列索引失败: {table.Name}.{defField.Name} v={lv}");
                }
                WriteLE(fixedBuf, (ulong)pi, plan.Width);
                return;
            }
            case BinRowPlanAnalyzer.KindStrU16:
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
            case BinRowPlanAnalyzer.KindBlob6:
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