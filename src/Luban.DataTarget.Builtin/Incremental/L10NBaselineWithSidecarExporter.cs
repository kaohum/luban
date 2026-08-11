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
using Luban.Checksum;
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

        // 合并所有语言表 -> perLang[lang] = key -> value（共享 util，与 checksum 注入一致）
        var perLang = L10NChecksumUtil.BuildPerLanguageMap(ctx, languages, keyFieldName);

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
        // per-language (ContentHash, Stamp)：内容相关戳，与 checksumconfig 注入共用同一份计算（ctx 缓存）
        var langStamps = ctx.GetL10NLangStamps();
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
            var stampInfo = langStamps.GetValueOrDefault(lang);
            sidecar.Languages[lang] = new LangSidecar
            {
                Hashes = hashes,
                ContentHash = stampInfo.ContentHash,
                Stamp = stampInfo.Stamp,
            };
        }

        // L10N 管线里的语言表（LanguageCode/LanguageText 等）也记 (ContentHash, Stamp)，供下次基准表级戳 gating
        foreach (var table in ctx.Tables)
        {
            if (table.Name == Checksum.ChecksumTableBuilder.ChecksumTableName)
            {
                continue;
            }
            if (string.IsNullOrEmpty(table.ContentHash))
            {
                continue;
            }
            sidecar.Tables[table.FullName] = new TableSidecarEntry
            {
                SignatureId = table.SignatureId,
                ContentHash = table.ContentHash,
                Stamp = table.Stamp,
            };
        }
        BaselineSidecarIO.SaveL10N(path, sidecar);
    }
}