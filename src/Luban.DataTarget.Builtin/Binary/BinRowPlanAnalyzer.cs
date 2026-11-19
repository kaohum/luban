// bin-row v3 列编码方案分析器：导出器（BinaryRowDataTarget）与代码生成（cs-bin 模板）共用同一份
// 进程内分析结果，保证"生成期常量"与"导出期数据"逐字节一致（PlanHash 校验兜底跨进程/陈旧场景）。

using System;
using System.Collections.Generic;
using Luban.Datas;
using Luban.Defs;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Binary;

public static class BinRowPlanAnalyzer
{
    public const byte KindRaw = 0;
    public const byte KindNarrow = 1;
    public const byte KindPalette = 2;
    public const byte KindStrU16 = 3;
    public const byte KindBlob6 = 5;

    public sealed class FieldPlanEntry
    {
        public byte Kind;
        public string Name;
        public byte Width;          // Raw: 原宽；Narrow: FOR 宽；Palette: 索引宽；StrU16=2；Blob6=6
        public bool Nullable;
        public long Min;            // Narrow 基准
        public long[] Palette;      // Palette 值表
        public byte PaletteValWidth;
        public Dictionary<long, int> PaletteIndex;
        public int Off;             // 行内偏移（含前置 nullable flag 位）

        public int PayloadOffset => Off + (Nullable ? 1 : 0);
    }

    public sealed class TablePlan
    {
        public byte[] PlanBytes;    // plan 段序列化（头内嵌 + 哈希源；自描述）
        public ulong Hash;          // FNV1a64(PlanBytes)
        public int Stride;
        public List<FieldPlanEntry> Fields;
        public Dictionary<string, FieldPlanEntry> FieldsByName;
    }

    // 导出按表并行（DataExporterBase 用 Task.Run），代码生成亦可能并发渲染——缓存必须线程安全
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, TablePlan> s_cache = new(StringComparer.Ordinal);

    public static TablePlan Get(DefTable table, List<Record> records)
    {
        string key = table.FullName;
        if (s_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        return s_cache.GetOrAdd(key, _ => Analyze(table, records ?? new List<Record>()));
    }

    private static TablePlan Analyze(DefTable table, List<Record> records)
    {
        var bean = table.ValueTType.DefBean;
        if (bean.IsAbstractType)
        {
            throw new Exception($"[bin-row] 多态值 bean 不支持: {table.Name}（定长行前提）");
        }

        // 字符串表计数（StrU16 判定所依据的全表字符串集合，含嵌套）
        var builder = new StringTableBuilder();
        var visitor = new BinaryDataVisitor { StringTable = builder };
        var tmp = new ByteBuf(1 << 10);
        foreach (var d in records)
        {
            d.Data.Apply(visitor, tmp);
        }

        var defFields = bean.HierarchyFields;
        var exported = new List<DefField>();
        var exportedIdx = new List<int>();
        for (int i = 0; i < defFields.Count; i++)
        {
            if (defFields[i].NeedExport())
            {
                exported.Add(defFields[i]);
                exportedIdx.Add(i);
            }
        }

        var fields = new List<FieldPlanEntry>(exported.Count);
        int stride = 0;
        for (int i = 0; i < exported.Count; i++)
        {
            var f = exported[i];
            var e = new FieldPlanEntry { Kind = KindRaw, Name = f.Name, Width = (byte)SlotWidth(f.CType), Nullable = f.CType.IsNullable };
            fields.Add(e);

            if (f.CType is TString)
            {
                // 字符串表索引字段（DString）与多语言 text-id（DInt，生成侧 int）区分
                bool isStringIndex = false;
                foreach (var r in records)
                {
                    var dbean = (DBean)r.Data;
                    var v = dbean.Fields[exportedIdx[i]];
                    if (v == null) continue;
                    isStringIndex = v is DString;
                    break;
                }
                if (isStringIndex && builder.Count <= 0xFFFF)
                {
                    e.Kind = KindStrU16;
                    e.Width = 2;
                }
            }
            else if (IsLongFamily(f.CType))
            {
                AnalyzeIntegerColumn(f, records, exportedIdx[i], e);
            }
            else if (IsVariableField(f.CType))
            {
                e.Kind = KindBlob6;
                e.Width = 6;
            }
            // bool/byte/float/double 保持 Raw

            e.Off = stride;
            stride += e.Width + (e.Nullable ? 1 : 0);
        }

        var plan = new TablePlan
        {
            Stride = stride,
            Fields = fields,
            FieldsByName = new Dictionary<string, FieldPlanEntry>(StringComparer.Ordinal),
        };
        foreach (var e in fields)
        {
            plan.FieldsByName[e.Name] = e;
        }
        plan.PlanBytes = BuildPlanBytes(fields);
        plan.Hash = Fnv1a64(plan.PlanBytes);
        return plan;
    }

    private static void AnalyzeIntegerColumn(DefField f, List<Record> records, int fieldIdx, FieldPlanEntry e)
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
            e.Kind = KindPalette;
            e.Width = (byte)iw;
            e.Palette = new long[distinct.Count];
            distinct.CopyTo(e.Palette);
            long pmin = long.MaxValue, pmax = long.MinValue;
            foreach (var pv in e.Palette) { if (pv < pmin) pmin = pv; if (pv > pmax) pmax = pv; }
            e.PaletteValWidth = pmin >= sbyte.MinValue && pmax <= sbyte.MaxValue ? (byte)1
                : pmin >= short.MinValue && pmax <= short.MaxValue ? (byte)2
                : pmin >= int.MinValue && pmax <= int.MaxValue ? (byte)4 : (byte)8;
            e.PaletteIndex = new Dictionary<long, int>(e.Palette.Length);
            for (int k = 0; k < e.Palette.Length; k++) e.PaletteIndex[e.Palette[k]] = k;
            return;
        }
        if (narrowOk)
        {
            e.Kind = KindNarrow;
            e.Width = (byte)nw;
            e.Min = min;
        }
    }

    private static bool IsLongFamily(TType t)
    {
        return t is TShort || t is TInt || t is TEnum || t is TLong || t is TDateTime
            || t is TDay || t is THour || t is TMinute || t is TSecond || t is TMillisecond;
    }

    private static bool IsVariableField(TType t)
    {
        return !(t is TBool) && !(t is TByte) && !(t is TShort) && !(t is TInt)
            && !(t is TLong) && !(t is TEnum) && !(t is TFloat) && !(t is TDouble)
            && !(t is TDateTime) && !(t is TDay) && !(t is THour) && !(t is TMinute)
            && !(t is TSecond) && !(t is TMillisecond) && !(t is TString);
    }

    public static bool TryGetLong(DType v, out long value)
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

    /// <summary>Raw 槽宽（schema 原生宽度；与 cs-bin 模板宽度表严格一致）。</summary>
    public static int SlotWidth(TType t)
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

    /// <summary>plan 段序列化（planCount + 每列 kind/name/width/nullable [+ Narrow min] [+ Palette valWidth/count/values]）。</summary>
    public static byte[] BuildPlanBytes(List<FieldPlanEntry> fields)
    {
        var planBuf = new ByteBuf(1 << 10);
        planBuf.WriteSize(fields.Count);
        foreach (var p in fields)
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

    public static ulong Fnv1a64(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte x in data)
        {
            h ^= x;
            h *= 1099511628211UL;
        }
        return h;
    }

    public static void WriteLE64(ByteBuf x, long v)
    {
        WriteLE32(x, (int)v);
        WriteLE32(x, (int)(v >> 32));
    }

    private static void WriteLE32(ByteBuf x, int v)
    {
        x.WriteByte((byte)v); x.WriteByte((byte)(v >> 8)); x.WriteByte((byte)(v >> 16)); x.WriteByte((byte)(v >> 24));
    }
}