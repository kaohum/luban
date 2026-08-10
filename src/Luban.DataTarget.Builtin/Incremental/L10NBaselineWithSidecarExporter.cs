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
using Luban.DataTarget;
using Luban.Datas;
using Luban.Defs;
using Luban.Incremental;
using Luban.Serialization;
using Luban.Types;
using Luban.TypeVisitors;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// L10N 基准导出 + 写 L10N sidecar + _l10n.checksum.bytes。
/// 包装 L10NBinarySplitDataExporter：先正常按语言拆分导出，再合并所有语言表的 (lang,key)->value，
/// 写 per-语言 key -> MD5(value) 的 sidecar，以及 per-语言整文件 MD5 的 _l10n.checksum.bytes。
/// 全语言共享一个 SignatureId（Language bean 结构签名）。
/// </summary>
[DataExporter("l10n-baseline-with-sidecar")]
public class L10NBaselineWithSidecarExporter : L10NBinarySplitDataExporter
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 正常 l10n-bin-split 导出（产 language/{lang}/languageconfig.bytes）
        base.Handle(ctx, dataTarget, manifest);

        // 2. 写 L10N sidecar + _l10n.checksum.bytes（失败不阻断导出）
        try
        {
            WriteL10NSidecar(ctx, manifest);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[l10n-baseline-with-sidecar] write sidecar failed: {e}");
        }
    }

    private void WriteL10NSidecar(GenerationContext ctx, OutputFileManifest manifest)
    {
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var languages = ctx.L10NLanguages;
        var keyFieldName = ctx.L10NTextKeyFieldName;

        // 合并所有语言表 -> perLang[lang] = key -> value（镜像 ExportL10NMergedPerLanguage 的合并语义）
        var perLang = new Dictionary<string, Dictionary<object, string>>();
        foreach (var lang in languages)
        {
            perLang[lang] = new Dictionary<object, string>();
        }

        DefField keyField = null;
        foreach (var table in ctx.Tables)
        {
            if (table.ValueTType is not TBean tbean)
            {
                continue;
            }
            var bean = tbean.DefBean;
            var kf = L10NBinarySplitDataExporter.FindField(bean, keyFieldName);
            if (kf == null || !L10NBinarySplitDataExporter.IsValidKeyType(kf.CType))
            {
                continue;
            }
            var langFields = L10NBinarySplitDataExporter.FindLanguageFields(bean, languages);
            if (langFields.Count == 0)
            {
                continue;
            }
            keyField ??= kf;

            foreach (var rec in ctx.GetTableExportDataList(table))
            {
                if (rec.Data is not DBean data)
                {
                    continue;
                }
                var key = L10NBinarySplitDataExporter.GetKeyValue(data.GetField(keyFieldName));
                if (key == null)
                {
                    continue;
                }
                if (key is string ks && string.IsNullOrEmpty(ks))
                {
                    continue;
                }
                foreach (var lf in langFields)
                {
                    perLang[lf.Name][key] = (data.GetField(lf.Name) as DString)?.Value ?? "";
                }
            }
        }

        // SignatureId：任取一张 L10N 表的行 bean 结构（全语言共享）
        string sigId = "";
        foreach (var table in ctx.Tables)
        {
            if (table.ValueTType is TBean)
            {
                sigId = StructureSignature.ComputeForTable(table);
                break;
            }
        }

        // 共享 key 集合（所有语言一致；取并集排序保证确定性）
        var keySet = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var map in perLang.Values)
        {
            foreach (var k in map.Keys)
            {
                keySet.Add(k.ToString());
            }
        }
        var keys = keySet.ToList();

        var sidecar = new L10NSidecar { SignatureId = sigId, Keys = keys };
        var langMd5 = new Dictionary<string, string>();
        foreach (var (lang, map) in perLang)
        {
            // key-string -> value，便于按下标对齐
            var mapStr = new Dictionary<string, string>(map.Count);
            foreach (var kv in map)
            {
                mapStr[kv.Key.ToString()] = kv.Value ?? "";
            }

            var hashes = new List<string>(keys.Count);
            foreach (var k in keys)
            {
                var v = mapStr.TryGetValue(k, out var val) ? val : "";
                hashes.Add(FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(v)));
            }
            sidecar.Languages[lang] = new LangSidecar { Hashes = hashes };

            // 整语言文件 MD5（镜像 SerializeDictionaryToBinary 布局）
            var buf = new ByteBuf();
            buf.WriteSize(map.Count);
            foreach (var kv in map)
            {
                if (keyField != null)
                {
                    L10NBinarySplitDataExporter.WriteKey(buf, kv.Key, keyField.CType);
                }
                buf.WriteString(kv.Value ?? "");
            }
            langMd5[lang] = FileUtil.CalcMD5(buf.CopyData());
        }
        BaselineSidecarIO.SaveL10N(path, sidecar);

        // _l10n.checksum.bytes（加进 manifest，随输出保存，FileCleaner 才不删）。
        // 相对 bin.outputDataDir 的默认名 _l10n.checksum.bytes，可用 -x incremental.l10nChecksumPath 覆盖相对名。
        var csName = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalL10NChecksumPath, true, "_l10n.checksum.bytes");
        if (!string.IsNullOrEmpty(csName))
        {
            var cs = new ByteBuf();
            cs.WriteString(sigId);
            cs.WriteSize(langMd5.Count);
            foreach (var kv in langMd5)
            {
                cs.WriteString(kv.Key);
                cs.WriteString(kv.Value);
            }
            manifest.AddFile(new OutputFile { File = csName, Content = cs.CopyData() });
        }
    }
}