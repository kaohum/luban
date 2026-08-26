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
using Luban.Incremental;
using Luban.Types;

namespace Luban.DataExporter.Builtin;

/// <summary>
/// 将带有多语言列的表按语言拆分成独立的二进制文件：
/// - 使用 ByteBuf 写出 {key, value} 映射（v2：key = 显式 int 语言 id，即 int 键紧凑字典，spec D3）
/// - 每种语言一个文件：{lang}/{TableName}.bytes（逐表）；配置 mergeOutput 时合并为 {lang}/{outputFile}.bytes
/// - 仅对 bin DataTarget 生效，其它 DataTarget 走默认逻辑
/// 使用方式：在 conf 中配置 dataExporter = "l10n-bin-split"，并配置 dataTargets 包含 "bin"。
/// </summary>
[DataExporter("l10n-bin-split")]
public class L10NBinarySplitDataExporter : DataExporterBase
{
    /// <summary>
    /// space 复用（omnibus-baseline 的非 indexMode space）时的语言覆盖；null = 用 ctx.L10NLanguages（旧行为）。
    /// </summary>
    internal IReadOnlyList<string> LanguageScope { get; set; }

    /// <summary>space 复用时的 key 字段覆盖；null = 用 ctx.L10NTextKeyFieldName（旧行为）。</summary>
    internal string KeyFieldScope { get; set; }

    /// <summary>space 复用时的输出根前缀（相对 dataTarget 输出根目录，即 space.OutputDir）；空 = 根目录（旧行为）。</summary>
    internal string OutputDirPrefix { get; set; } = "";

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

    internal static DefField FindField(DefBean bean, string fieldName)
    {
        return bean.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal));
    }

    internal static List<DefField> FindLanguageFields(DefBean bean, IReadOnlyList<string> languages)
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

    internal static string BuildLanguageFilePath(string lang, DefTable table)
    {
        // 目录：{lang}/{TableOutputName}.bytes
        string fileName = $"{table.OutputDataFile}.bytes";
        return Path.Combine(lang, fileName);
    }

    internal static bool IsValidKeyType(TType type)
    {
        return type is TString || type is TInt || type is TLong || type is TShort || type is TByte;
    }

    internal static object GetKeyValue(DType data)
    {
        return data.GetValueObject();
    }

    internal static byte[] SerializeDictionaryToBinary(Dictionary<object, string> dict, TType keyType)
    {
        // 与 L10NChecksumUtil.SerializeLanguageBytes 逐字节同布局（checksum 指纹口径 = 实际文件，
        // spec 2026-08-25 §4.4：字符串表 + 索引引用）；实现委托给后者，避免两处序列化漂移。
        return L10NChecksumUtil.SerializeLanguageBytes(dict, keyType);
    }

    internal static void ExportL10NTablePerLanguage(DefTable table, List<Record> records,
        string keyFieldName, IReadOnlyList<string> languages, OutputFileManifest manifest, string outputDirPrefix = null)
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
            if (!string.IsNullOrEmpty(outputDirPrefix))
            {
                path = Path.Combine(outputDirPrefix, path);
            }

            manifest.AddFile(new OutputFile
            {
                File = path,
                Content = bytes,
            });
        }
    }

    /// <summary>
    /// 把多张多语言表的记录按语言合并进同一个二进制文件：{outputDirPrefix}/{lang}/{outputFileName}.bytes。
    /// 典型场景：LanguageCode 表（代码引用 key）与 LanguageText 表（策划文本 key）合并导出一个运行时 bin。
    /// v2（spec 2026-08-22）：语言 key 为显式 int（keyType=TInt），序列化即 int 键紧凑字典
    /// [字符串表][WriteSize: pairCount][WriteKey(id) WriteSize(valueIndex)]*（v3 spec 2026-08-25）——indexMode space（main/aot）与
    /// server space 统一走本路径，数组格式（ExportL10NArrayPerLanguage）已退役删除。
    /// outputDirPrefix 供 omnibus 的 space 路由传入 space.OutputDir；null/空 = 根目录（旧行为）。
    /// </summary>
    internal static void ExportL10NMergedPerLanguage(GenerationContext ctx, IReadOnlyList<DefTable> tables,
        string keyFieldName, IReadOnlyList<string> languages, OutputFileManifest manifest, string outputFileName,
        string outputDirPrefix = null)
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
            string path = string.IsNullOrEmpty(outputDirPrefix)
                ? Path.Combine(lang, outputFileName + ".bytes")
                : Path.Combine(outputDirPrefix, lang, outputFileName + ".bytes");

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

        var languages = LanguageScope ?? ctx.L10NLanguages;
        // 未配置任何语言时，完全复用默认行为
        if (languages.Count == 0)
        {
            base.Handle(ctx, dataTarget, manifest);
            return;
        }

        string keyFieldName = KeyFieldScope ?? ctx.L10NTextKeyFieldName;
        bool keepMerged = KeepMergedBin();

        // TableFilter != null 表示被 omnibus-baseline 以 space 范围复用（只保留该 space 的表）；
        // 此时 mergeOutput（全局合并文件名）不适用，根目录 checksumconfig 也由组合导出器按 space 拆分输出。
        bool spaceScoped = TableFilter != null;
        var tables = SelectTables(ctx, dataTarget);

        // 配置了 l10n.mergeOutput：把多张语言表按语言合并导出单一 bin，跳过逐表逻辑
        string mergeOutput = spaceScoped ? null : GetMergeOutputFile();
        if (!string.IsNullOrWhiteSpace(mergeOutput))
        {
            ExportL10NMergedPerLanguage(ctx, tables, keyFieldName, languages, manifest, mergeOutput);
            // 语言管线也导出 checksum 表（含 per-language 行），供前端/服务器按语言比对
            ExportChecksumTable(ctx, dataTarget, tables, manifest);
            return;
        }

        foreach (var table in tables)
        {
            var records = ctx.GetTableExportDataList(table);

            // 先尝试按语言拆分导出（space 复用时输出根 = space.OutputDir）
            ExportL10NTablePerLanguage(table, records, keyFieldName, languages, manifest, OutputDirPrefix);

            // 可选：是否保留原始"合并语言"的二进制文件
            if (keepMerged)
            {
                var defaultFile = dataTarget.ExportTable(table, records);
                if (defaultFile != null)
                {
                    if (!string.IsNullOrEmpty(OutputDirPrefix))
                    {
                        defaultFile = new OutputFile
                        {
                            File = Path.Combine(OutputDirPrefix, defaultFile.File),
                            Content = defaultFile.Content,
                            Encoding = defaultFile.Encoding,
                        };
                    }
                    manifest.AddFile(defaultFile);
                }
            }
        }

        if (spaceScoped)
        {
            // space 范围运行：根目录 checksumconfig 由 omnibus-baseline 统一输出（space 目录 + 仅语言行），
            // 此处跳过，避免与普通表管线的根目录 checksumconfig 重复。
            return;
        }
        // 语言管线也导出 checksum 表（含 per-language 行），供前端/服务器按语言比对
        ExportChecksumTable(ctx, dataTarget, tables, manifest);
    }

    /// <summary>
    /// 把 ChecksumConfig 表作为标准 bin 导出（checksumconfig.bytes）。
    /// 语言管线里该表已由 GenerationContext 注入 per-language 行（TableName=语言名），
    /// 前端/服务器用现有 ChecksumConfig 类按语言名读取。
    /// </summary>
    private static void ExportChecksumTable(GenerationContext ctx, IDataTarget dataTarget, IEnumerable<DefTable> tables, OutputFileManifest manifest)
    {
        foreach (var table in tables)
        {
            if (table.Name != Checksum.ChecksumTableBuilder.ChecksumTableName)
            {
                continue;
            }
            var records = ctx.GetTableExportDataList(table);
            if (records == null || records.Count == 0)
            {
                continue;
            }
            var file = dataTarget.ExportTable(table, records);
            if (file != null)
            {
                manifest.AddFile(file);
            }
            break;
        }
    }
}


