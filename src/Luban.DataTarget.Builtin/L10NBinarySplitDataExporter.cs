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
    private static IReadOnlyList<string> GetLanguages()
    {
        // 例：-x l10n.languages=zh_CN,en_US,ja_JP
        string langs = EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily, "languages", false, "");
        if (string.IsNullOrWhiteSpace(langs))
        {
            return Array.Empty<string>();
        }

        return langs
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    private static string GetKeyFieldName()
    {
        // 复用 textFile.keyFieldName 配置；如未设置则使用常见的 id 作为默认 key 字段
        return EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily,
            BuiltinOptionNames.L10NTextFileKeyFieldName, false, "id");
    }

    private static bool KeepMergedBin()
    {
        // 是否保留原始“合并语言”的二进制（默认为 false，只导出按语言拆分后的文件）
        return EnvManager.Current.GetBoolOptionOrDefault(BuiltinOptionNames.L10NFamily,
            "keepMergedBin", false, false);
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

    private static byte[] SerializeDictionaryToBinary(Dictionary<string, string> dict)
    {
        var buf = new ByteBuf();
        buf.WriteSize(dict.Count);
        foreach (var kv in dict)
        {
            buf.WriteString(kv.Key);
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
        if (keyField == null || keyField.CType is not TString)
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
            var map = new Dictionary<string, string>();

            foreach (var (_, data) in beanRecords)
            {
                var keyValue = data.GetField(keyFieldName) as DString;
                var langValue = data.GetField(langField.Name) as DString;
                if (keyValue == null)
                {
                    continue;
                }

                string key = keyValue.Value;
                if (string.IsNullOrEmpty(key))
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

            byte[] bytes = SerializeDictionaryToBinary(map);
            string path = BuildLanguageFilePath(langField.Name, table);

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

        var languages = GetLanguages();
        // 未配置任何语言时，完全复用默认行为
        if (languages.Count == 0)
        {
            base.Handle(ctx, dataTarget, manifest);
            return;
        }

        string keyFieldName = GetKeyFieldName();
        bool keepMerged = KeepMergedBin();

        var tables = dataTarget.ExportAllRecords ? ctx.Tables : ctx.ExportTables;

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


