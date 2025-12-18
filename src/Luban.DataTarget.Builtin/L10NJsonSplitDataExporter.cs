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
using System.Text.Json;
using Luban.DataExporter.Builtin.Json;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Datas;
using Luban.Types;
using Luban.Utils;

namespace Luban.DataExporter.Builtin;

/// <summary>
/// 将带有多语言列的表按语言拆分成独立的 json 文件：
/// - 通过 l10n 选项族配置 key 字段名和语言列名列表
/// - 对包含这些列的表，每个语言列生成一个 {lang}/{TableName}.json
/// - 其他表仍然走默认 json 导出逻辑
/// 使用方式：在 conf 中配置 dataExporter = "l10n-json-split"
/// 并使用 json 相关 dataTarget（如 json、json2 等）。
/// </summary>
[DataExporter("l10n-json-split")]
public class L10NJsonSplitDataExporter : DataExporterBase
{
    private static bool KeepMergedJson()
    {
        // 是否保留原始“合并语言”的 json（默认为 false，只导出按语言拆分后的文件）
        return EnvManager.Current.GetBoolOptionOrDefault(BuiltinOptionNames.L10NFamily,
            "keepMergedJson", false, false);
    }

    private static bool IsJsonBasedTarget(IDataTarget dataTarget)
    {
        // 仅对 Json 系列 DataTarget 做特殊处理，其他 DataTarget 走默认逻辑
        return dataTarget is JsonDataTarget;
    }

    private static DefField? FindField(DefBean bean, string fieldName)
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
        // 目录：{lang}/{TableOutputName}.json
        string fileName = $"{table.OutputDataFile}.json";
        return Path.Combine(lang, fileName);
    }

    private static string SerializeDictionaryToJson(Dictionary<string, string> dict)
    {
        var options = new JsonSerializerOptions
        {
            WriteIndented = !JsonDataTarget.UseCompactJson,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        };
        return JsonSerializer.Serialize(dict, options);
    }

    private static void ExportL10NTablePerLanguage(GenerationContext ctx, DefTable table, List<Record> records,
        string keyFieldName, IReadOnlyList<string> languages, IDataTarget dataTarget, OutputFileManifest manifest)
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

        var encoding = dataTarget.FileEncoding;

        foreach (var langField in languageFields)
        {
            var map = new Dictionary<string, string>();

            foreach (var (record, data) in beanRecords)
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

            string json = SerializeDictionaryToJson(map);
            string path = BuildLanguageFilePath(langField.Name, table);

            manifest.AddFile(new OutputFile
            {
                File = path,
                Content = json,
                Encoding = encoding,
            });
        }
    }

    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 非 Json 系列 DataTarget，保持默认行为
        if (!IsJsonBasedTarget(dataTarget))
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
        bool keepMerged = KeepMergedJson();

        var tables = dataTarget.ExportAllRecords ? ctx.Tables : ctx.ExportTables;

        foreach (var table in tables)
        {
            var records = ctx.GetTableExportDataList(table);

            // 先尝试按语言拆分导出
            ExportL10NTablePerLanguage(ctx, table, records, keyFieldName, languages, dataTarget, manifest);

            // 可选：是否保留原始“合并语言”的 json 文件
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


