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

using System;
using System.Collections.Generic;
using System.Linq;
using Luban.Datas;
using Luban.Defs;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;

namespace Luban.Incremental;

/// <summary>
/// L10N 语言数据共享工具：把多张语言表合并为 per-语言 (key -> value) 映射，
/// 并序列化/计算整语言文件 MD5（与 l10n-bin-split 输出字节一致）。
/// 供两处共用：
/// 1) GenerationContext 的 checksum 注入阶段（per-language 行进 ChecksumConfig，供前端/服务器上报）；
/// 2) L10N 基准/增量导出器（sidecar 的 per-语言 key 哈希、整语言文件 MD5）。
/// </summary>
public static class L10NChecksumUtil
{
    /// <summary>
    /// 合并语言表 -> perLang[lang] = key -> value（镜像 ExportL10NMergedPerLanguage 的合并语义）。
    /// tables = null（默认）遍历全部表（旧行为）；传入则只合并名单内的表（space 限定版）。
    /// </summary>
    public static Dictionary<string, Dictionary<object, string>> BuildPerLanguageMap(
        GenerationContext ctx, IReadOnlyList<string> languages, string keyFieldName, IEnumerable<DefTable> tables = null)
    {
        var perLang = new Dictionary<string, Dictionary<object, string>>();
        foreach (var lang in languages)
        {
            perLang[lang] = new Dictionary<object, string>();
        }

        foreach (var table in tables ?? ctx.Tables)
        {
            if (table.ValueTType is not TBean tbean)
            {
                continue;
            }
            var bean = tbean.DefBean;
            var keyField = FindField(bean, keyFieldName);
            if (keyField == null || !IsValidKeyType(keyField.CType))
            {
                continue;
            }
            var langFields = FindLanguageFields(bean, languages);
            if (langFields.Count == 0)
            {
                continue;
            }

            foreach (var rec in ctx.GetTableExportDataList(table))
            {
                if (rec.Data is not DBean data)
                {
                    continue;
                }
                var key = GetKeyValue(data.GetField(keyFieldName));
                if (key == null)
                {
                    continue;
                }
                if (key is string s && string.IsNullOrEmpty(s))
                {
                    continue;
                }
                foreach (var lf in langFields)
                {
                    perLang[lf.Name][key] = (data.GetField(lf.Name) as DString)?.Value ?? "";
                }
            }
        }
        return perLang;
    }

    /// <summary>
    /// 取第一张 L10N 表的 key 类型（全语言共享，用于序列化对齐）。
    /// tables = null（默认）遍历全部表（旧行为）；传入则只在名单内找（space 限定版）。
    /// </summary>
    public static TType FindLanguageKeyType(GenerationContext ctx, string keyFieldName, IEnumerable<DefTable> tables = null)
    {
        foreach (var table in tables ?? ctx.Tables)
        {
            if (table.ValueTType is not TBean tbean)
            {
                continue;
            }
            var keyField = FindField(tbean.DefBean, keyFieldName);
            if (keyField != null && IsValidKeyType(keyField.CType))
            {
                return keyField.CType;
            }
        }
        return null;
    }

    /// <summary>
    /// 计算每种语言的整语言文件 MD5（字节布局与 l10n-bin-split 输出一致）。
    /// </summary>
    public static Dictionary<string, string> ComputePerLanguageFileMd5(
        GenerationContext ctx, IReadOnlyList<string> languages, string keyFieldName)
    {
        var perLang = BuildPerLanguageMap(ctx, languages, keyFieldName);
        var keyType = FindLanguageKeyType(ctx, keyFieldName);

        var result = new Dictionary<string, string>(perLang.Count);
        foreach (var (lang, map) in perLang)
        {
            result[lang] = FileUtil.CalcMD5(SerializeLanguageBytes(map, keyType));
        }
        return result;
    }

    /// <summary>
    /// space 限定版：计算 space 每种语言的整语言文件 MD5。
    /// v2（spec 2026-08-22）：main/aot/server 三类 space 统一走 int 字典序列化
    /// （v3 spec 2026-08-25 起为 [字符串表][pairCount][WriteKey(id) WriteSize(valueIndex)]*，
    /// 与 {outputDir} 下各语言 bin 布局一致——
    /// indexMode space 为合并单文件 {lang}/{outputFile}.bytes，非 indexMode space 为逐表 {lang}/{table}.bytes，
    /// 序列化布局相同）。数组序列化（SerializeLanguageArray）已随 v2 退役删除。
    /// space.Tables == null 表示"全部表"（omnibus 旧路径合成 space 专用），与旧全局行为等价。
    /// </summary>
    public static Dictionary<string, string> ComputePerLanguageFileMd5(GenerationContext ctx, L10N.L10NSpace space)
    {
        IEnumerable<DefTable> tables = space.Tables == null
            ? null
            : L10N.L10NKeyIndexBuilder.MatchTables(ctx, space);
        var perLang = BuildPerLanguageMap(ctx, space.Languages, space.KeyFieldName, tables);

        var keyType = FindLanguageKeyType(ctx, space.KeyFieldName, tables);
        var result = new Dictionary<string, string>(perLang.Count);
        foreach (var (lang, map) in perLang)
        {
            result[lang] = FileUtil.CalcMD5(SerializeLanguageBytes(map, keyType));
        }
        return result;
    }

    /// <summary>
    /// 把 per-语言 (key -> value) 序列化为与 languageconfig.bytes 逐条一致的字节（spec 2026-08-25 §4.4）：
    /// [字符串表][WriteSize: pairCount][WriteKey(key) 或 WriteSize(keyIndex)][WriteSize(valueIndex)]*
    /// 字符串表内容：所有 value 文本；string-key space 的 key 字符串也入表（int key 不入表）。
    /// v2 语言 key 为显式 int（keyType=TInt），即 int 键紧凑字典格式（server space 同款，spec D3）。
    /// </summary>
    public static byte[] SerializeLanguageBytes(Dictionary<object, string> map, TType keyType)
    {
        var builder = new StringTableBuilder();
        bool stringKey = keyType is TString;
        if (stringKey)
        {
            foreach (var kv in map) builder.GetOrAddIndex((string)kv.Key);
        }
        foreach (var kv in map) builder.GetOrAddIndex(kv.Value);

        var buf = new ByteBuf();
        builder.Write(buf);
        buf.WriteSize(map.Count);
        foreach (var kv in map)
        {
            if (stringKey)
            {
                buf.WriteSize(builder.GetOrAddIndex((string)kv.Key));
            }
            else
            {
                WriteKey(buf, kv.Key, keyType);
            }
            buf.WriteSize(builder.GetOrAddIndex(kv.Value));
        }
        return buf.CopyData();
    }

    /// <summary>
    /// 装箱 key（BuildPerLanguageMap 的 Dictionary&lt;object,string&gt;）→ int 语言 id。
    /// v2 语言表 key 字段必为 int 家族；string key 属 v1 残留，显式报错。
    /// </summary>
    public static int ToIntId(object key)
    {
        return key switch
        {
            int i => i,
            long l => (int)l,
            short s => s,
            byte b => b,
            _ => throw new NotSupportedException($"[l10n] v2 语言 key 必须是 int（显式语言 id），遇到 {key?.GetType().Name}: {key}"),
        };
    }

    private static void WriteKey(ByteBuf buf, object key, TType type)
    {
        switch (type)
        {
            case TString: buf.WriteString((string)key); break;
            case TInt: buf.WriteInt((int)key); break;
            case TLong: buf.WriteLong((long)key); break;
            case TShort: buf.WriteShort((short)key); break;
            case TByte: buf.WriteByte((byte)key); break;
            default: throw new NotSupportedException($"Unsupported key type: {type.GetType().Name}");
        }
    }

    private static DefField FindField(DefBean bean, string fieldName)
    {
        return bean.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal));
    }

    private static List<DefField> FindLanguageFields(DefBean bean, IReadOnlyList<string> languages)
    {
        var result = new List<DefField>();
        foreach (var lang in languages)
        {
            var f = bean.Fields.FirstOrDefault(x =>
                string.Equals(x.Name, lang, StringComparison.Ordinal) && x.CType is TString);
            if (f != null)
            {
                result.Add(f);
            }
        }
        return result;
    }

    private static bool IsValidKeyType(TType type)
    {
        return type is TString || type is TInt || type is TLong || type is TShort || type is TByte;
    }

    private static object GetKeyValue(DType data)
    {
        return data.GetValueObject();
    }
}