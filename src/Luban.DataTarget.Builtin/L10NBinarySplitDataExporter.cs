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
using System.IO;
using System.Linq;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Datas;
using Luban.Serialization;
using Luban.Types;

namespace Luban.DataExporter.Builtin;

/// <summary>
/// 将带有多语言列的表按语言拆分成独立的二进制文件：
/// - 使用 ByteBuf 写出简单的 {key:string, value:string} 映射
/// - 每种语言一个文件：{lang}/{TableName}.bytes
/// - 仅对 bin DataTarget 生效，其它 DataTarget 走默认逻辑
/// 使用方式：在 conf 中配置 dataExporter = "l10n-bin-split"，并配置 dataTargets 包含 "bin"。
/// </summary>
[DataExporter("l10n-bin-split")]
public class L10NBinarySplitDataExporter : DataExporterBase
{
    private static bool KeepMergedBin()
    {
        // 是否保留原始“合并语言”的二进制（默认为 false，只导出按语言拆分后的文件）
        return EnvManager.Current.GetBoolOptionOrDefault(BuiltinOptionNames.L10NFamily,
            "keepMergedBin", false, false);
    }

    /// <summary>
    /// 配置了 l10n.mergeOutput 时，把所有导出的多语言表的记录按语言合并进同一个二进制文件。
    /// 用于“多张语言表（如代码引用表 + 策划文本表）合并导出一个 bin”的场景。
    /// 未配置时返回 null，保持原有逐表导出行为。
    /// </summary>
    private static string GetMergeOutputFile()
    {
        return EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily,
            "mergeOutput", false, null);
    }

    private static bool IsBinaryTarget(IDataTarget dataTarget)
    {
        // 仅对 bin DataTarget 做特殊处理，其他 DataTarget 走默认逻辑
        return dataTarget is DataExporter.Builtin.Binary.BinaryDataTarget;
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

    private static string BuildLanguageFilePath(string lang, DefTable table)
    {
        // 目录：{lang}/{TableOutputName}.bytes
        string fileName = $"{table.OutputDataFile}.bytes";
        return Path.Combine(lang, fileName);
    }

    private static bool IsValidKeyType(TType type)
    {
        return type is TString || type is TInt || type is TLong || type is TShort || type is TByte;
    }

    private static object GetKeyValue(DType data)
    {
        return data.GetValueObject();
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

    private static byte[] SerializeDictionaryToBinary(Dictionary<object, string> dict, TType keyType)
    {
        var buf = new ByteBuf();
        buf.WriteSize(dict.Count);
        foreach (var kv in dict)
        {
            WriteKey(buf, kv.Key, keyType);
            buf.WriteString(kv.Value ?? string.Empty);
        }
        return buf.CopyData();
    }

    private static void ExportL10NTablePerLanguage(DefTable table, List<Record> records,
        string keyFieldName, IReadOnlyList<string> languages, OutputFileManifest manifest)
    {
        if (table.ValueTType is not TBean tbean)
        {
            return;
        }

        var bean = tbean.DefBean;
        var keyField = FindField(bean, keyFieldName);
        if (keyField == null || !IsValidKeyType(keyField.CType))
        {
            return;
        }

        var languageFields = FindLanguageFields(bean, languages);
        if (languageFields.Count == 0)
        {
            return;
        }

        // 预先确保都是 DBean 结构
        var beanRecords = records
            .Where(r => r.Data is DBean)
            .Select(r => (Record: r, Bean: (DBean)r.Data))
            .ToList();

        if (beanRecords.Count == 0)
        {
            return;
        }

        foreach (var langField in languageFields)
        {
            var map = new Dictionary<object, string>();

            foreach (var (_, data) in beanRecords)
            {
                var dKey = data.GetField(keyFieldName);
                var langValue = data.GetField(langField.Name) as DString;
                
                object key = GetKeyValue(dKey);
                if (key == null)
                {
                    continue;
                }
                
                // For string keys, check empty
                if (key is string s && string.IsNullOrEmpty(s))
                {
                    continue;
                }

                string value = langValue?.Value ?? string.Empty;
                map[key] = value;
            }

            if (map.Count == 0)
            {
                continue;
            }

            byte[] bytes = SerializeDictionaryToBinary(map, keyField.CType);
            string path = BuildLanguageFilePath(langField.Name, table);

            manifest.AddFile(new OutputFile
            {
                File = path,
                Content = bytes,
            });
        }
    }

    /// <summary>
    /// 把多张多语言表的记录按语言合并进同一个二进制文件：{lang}/{outputFileName}.bytes。
    /// 典型场景：LanguageCode 表（代码引用 key）与 LanguageText 表（策划文本 key）合并导出一个运行时 bin。
    /// </summary>
    private static void ExportL10NMergedPerLanguage(GenerationContext ctx, IReadOnlyList<DefTable> tables,
        string keyFieldName, IReadOnlyList<string> languages, OutputFileManifest manifest, string outputFileName)
    {
        // 仅保留 value 为 bean、含合法 key 字段、且至少有一个语言字段的表
        var mergedTables = new List<(DefTable Table, DefBean Bean, DefField KeyField)>();
        foreach (var table in tables)
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
            if (FindLanguageFields(bean, languages).Count == 0)
            {
                continue;
            }
            mergedTables.Add((table, bean, keyField));
        }

        if (mergedTables.Count == 0)
        {
            return;
        }

        // 多张表共用同一 Language bean，key 类型一致
        TType unifiedKeyType = mergedTables[0].KeyField.CType;

        // 安全阀：跨表重复 key 直接报错。同一 key 只允许出现在一张语言表里，
        // 杜绝“代码表与文本表各存一份翻译”导致的漂移。
        var seenKeys = new HashSet<object>();
        foreach (var (table, _, _) in mergedTables)
        {
            foreach (var r in ctx.GetTableExportDataList(table))
            {
                if (r.Data is not DBean data)
                {
                    continue;
                }
                object key = GetKeyValue(data.GetField(keyFieldName));
                if (key == null)
                {
                    continue;
                }
                if (key is string s && string.IsNullOrEmpty(s))
                {
                    continue;
                }
                if (!seenKeys.Add(key))
                {
                    throw new Exception(
                        $"[l10n-merge] 多语言表之间存在重复 key '{key}'（见于表 {table.FullName}）。" +
                        "合并导出要求各语言表的 key 互斥，请排查是否在 LanguageCode/LanguageText 中重复填写了同一 key。");
                }
            }
        }

        foreach (var lang in languages)
        {
            var map = new Dictionary<object, string>();
            foreach (var (table, bean, _) in mergedTables)
            {
                // 该表若无此语言列（按字段名+string 类型）则跳过
                if (FindField(bean, lang)?.CType is not TString)
                {
                    continue;
                }
                foreach (var r in ctx.GetTableExportDataList(table))
                {
                    if (r.Data is not DBean data)
                    {
                        continue;
                    }
                    object key = GetKeyValue(data.GetField(keyFieldName));
                    if (key == null)
                    {
                        continue;
                    }
                    if (key is string s && string.IsNullOrEmpty(s))
                    {
                        continue;
                    }
                    var langValue = data.GetField(lang) as DString;
                    map[key] = langValue?.Value ?? string.Empty;
                }
            }

            if (map.Count == 0)
            {
                continue;
            }

            byte[] bytes = SerializeDictionaryToBinary(map, unifiedKeyType);
            string path = Path.Combine(lang, outputFileName + ".bytes");

            manifest.AddFile(new OutputFile
            {
                File = path,
                Content = bytes,
            });
        }
    }

    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 非 bin DataTarget，保持默认行为
        if (!IsBinaryTarget(dataTarget))
        {
            base.Handle(ctx, dataTarget, manifest);
            return;
        }

        var languages = ctx.L10NLanguages;
        // 未配置任何语言时，完全复用默认行为
        if (languages.Count == 0)
        {
            base.Handle(ctx, dataTarget, manifest);
            return;
        }

        string keyFieldName = ctx.L10NTextKeyFieldName;
        bool keepMerged = KeepMergedBin();

        var tables = dataTarget.ExportAllRecords ? ctx.Tables : ctx.ExportTables;

        // 配置了 l10n.mergeOutput：把多张语言表按语言合并导出单一 bin，跳过逐表逻辑
        string mergeOutput = GetMergeOutputFile();
        if (!string.IsNullOrWhiteSpace(mergeOutput))
        {
            ExportL10NMergedPerLanguage(ctx, tables, keyFieldName, languages, manifest, mergeOutput);
            return;
        }

        foreach (var table in tables)
        {
            var records = ctx.GetTableExportDataList(table);

            // 先尝试按语言拆分导出
            ExportL10NTablePerLanguage(table, records, keyFieldName, languages, manifest);

            // 可选：是否保留原始“合并语言”的二进制文件
            if (keepMerged)
            {
                var defaultFile = dataTarget.ExportTable(table, records);
                if (defaultFile != null)
                {
                    manifest.AddFile(defaultFile);
                }
            }
        }
    }
}


