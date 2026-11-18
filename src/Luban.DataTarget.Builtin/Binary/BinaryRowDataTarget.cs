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

namespace Luban.DataExporter.Builtin.Binary;

/// <summary>
/// 二进制行表导出（定长行 + 共享池去重，客户端按需读表专用；紧凑模式 bin 不受影响）。
/// 布局：[magic "TBIX" 4B][BE i32 headerLen][BE i32 restLen]
/// [header: signature(varint+utf8) + 字符串表(与紧凑模式同编码) + n(varint)]
/// [fixed region: n × stride，导出原序，packed 无对齐，多字节标量小端]
///     标量原生宽度 | string 字段 4B 字符串表索引 | 变长字段(bean/list/array/set/map) 8B (off i32, len i32) → shared pool
///     nullable 字段槽 = 1B flag + 载荷（flag=0 时载荷字节仍占位，保持 stride 恒定）
/// [shared pool: 去重后的变长 blob（紧凑编码），内容寻址，相同字节只存一份]
/// 行 i 字段 j 的位置 = fixedBase + i×stride + offset_j（offset_j/stride 为生成期常量，与 cs-bin 模板宽度表严格一致）。
/// 多态值 bean 不支持（本项目实测 0 张）；行内字符串（含嵌套）统一走字符串表，blob 字节确定 → 去重可靠。
/// </summary>
[DataTarget("bin-row")]
public class BinaryRowDataTarget : DataTargetBase
{
    protected override string DefaultOutputFileExt => "bytes";

    private static readonly byte[] Magic = { (byte)'T', (byte)'B', (byte)'I', (byte)'X' };

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

        // 1. 第一遍：注册全表字符串（含嵌套），丢弃输出
        var tmp = new ByteBuf(1 << 10);
        foreach (var d in records)
        {
            d.Data.Apply(visitor, tmp);
        }

        // 2. header：签名 + 字符串表（现有编码）+ n
        var headerBuf = new ByteBuf(1 << 10);
        headerBuf.WriteString(table.SignatureId);
        builder.Write(headerBuf);
        headerBuf.WriteSize(records.Count);

        // 3. 行装配：标量/string 入 fixed；变长字段紧凑序列化 → 内容寻址去重 → pool
        int stride = 0;
        var defFields = bean.HierarchyFields;
        foreach (var f in defFields)
        {
            if (!f.NeedExport()) continue;
            stride += SlotWidth(f.CType) + (f.CType.IsNullable ? 1 : 0);
        }

        var fixedBuf = new ByteBuf(records.Count * stride + 16);
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
            foreach (var field in dbean.Fields)
            {
                var defField = (DefField)defFields[index++];
                if (!defField.NeedExport()) continue;
                WriteSlot(defField, field, visitor, scratch, fixedBuf, poolBuf, poolIndex, builder);
            }
        }

        if (fixedBuf.Size != (long)records.Count * stride)
        {
            throw new Exception($"[bin-row] 行装配错位: {table.Name} fixed={fixedBuf.Size} expect={records.Count * stride}");
        }

        // 4. 组装
        var bytes = new ByteBuf((int)(12 + headerBuf.Size + fixedBuf.Size + poolBuf.Size));
        bytes.WriteBytesWithoutSize(Magic);
        WriteBE32(bytes, headerBuf.Size);
        WriteBE32(bytes, fixedBuf.Size + poolBuf.Size);
        bytes.WriteBytesWithoutSize(headerBuf.CopyData());
        bytes.WriteBytesWithoutSize(fixedBuf.CopyData());
        bytes.WriteBytesWithoutSize(poolBuf.CopyData());
        return CreateOutputFile($"{table.OutputDataFile}.{OutputFileExt}", bytes.CopyData());
    }

    /// <summary>写一个字段槽（宽度表与 cs-bin 生成模板严格一致；nullable = 1B flag + 载荷占位）。</summary>
    private void WriteSlot(DefField defField, DType field, BinaryDataVisitor visitor, ByteBuf scratch,
        ByteBuf fixedBuf, ByteBuf poolBuf, Dictionary<string, (int off, int len)> poolIndex, StringTableBuilder builder)
    {
        bool nullable = defField.CType.IsNullable;
        if (nullable)
        {
            fixedBuf.WriteByte(field != null ? (byte)1 : (byte)0);
        }
        if (field == null)
        {
            // nullable null：载荷占位（保持 stride）
            for (int i = 0; i < SlotWidth(defField.CType); i++) fixedBuf.WriteByte(0);
            return;
        }

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
                // 多语言 text-id 字段（值类型语言 key）：数据为 DInt，生成代码侧为 int，槽宽同为 4B
                if (field is DString ds)
                {
                    WriteLE32(fixedBuf, builder.GetOrAddIndex(ds.Value));
                }
                else
                {
                    WriteLE32(fixedBuf, ((DInt)field).Value);
                }
                break;
            default:
                // 变长（bean/list/array/set/map）：紧凑序列化（字符串走表索引）→ 内容寻址去重 → pool 指针
                // nullable 槽的读取端生成 if(ReadBool()){值} 前缀约定——blob 内容须带 bool true 前缀对齐
                scratch.Clear();
                if (nullable) scratch.WriteBool(true);
                field.Apply(visitor, scratch);
                var blob = scratch.CopyData();
                string hash;
                {
                    // SHA1 全量内容哈希（导出期一次性，性能足够）
#if NET8_0_OR_GREATER
                    hash = Convert.ToHexString(SHA1.HashData(blob));
#else
                    using var sha = SHA1.Create();
                    hash = Convert.ToHexString(sha.ComputeHash(blob));
#endif
                }
                if (!poolIndex.TryGetValue(hash, out var slot))
                {
                    slot = (poolBuf.Size, blob.Length);
                    poolBuf.WriteBytesWithoutSize(blob);
                    poolIndex[hash] = slot;
                }
                WriteLE32(fixedBuf, slot.off);
                WriteLE32(fixedBuf, slot.len);
                break;
        }
    }

    /// <summary>槽宽（不含 nullable flag；与 cs-bin 模板宽度表严格一致）。</summary>
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
            case TString: return 4; // 字符串表索引
            default: return 8;      // bean/list/array/set/map → 池指针
        }
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
