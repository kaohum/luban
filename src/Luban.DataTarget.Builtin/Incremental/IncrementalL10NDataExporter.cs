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
using System.Text;
using System.Text.Json;
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
/// L10N 增量导出器。
/// 读 L10N sidecar -> 结构 gate（Language bean SignatureId）-> per-语言 key->value 行级 diff -> 出 LLP1 patch + _l10n.delta.manifest。
/// diff 单位 = (语言, 文本 key)；新增 key -> 所有语言各 upsert；删除 key -> 所有语言各 delete；改某语言文案 -> 只该语言 upsert。
/// </summary>
[DataExporter("incremental-l10n-bin-split")]
public class IncrementalL10NDataExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var sidecarPath = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(sidecarPath))
        {
            throw new InvalidOperationException("[incremental-l10n] 未配置 incremental.sidecarPath，无法 diff。请先跑基准导出。");
        }

        var baseline = BaselineSidecarIO.LoadL10N(sidecarPath);

        // 结构 gate：全语言共享一个 SignatureId
        string curSig = "";
        foreach (var t in ctx.Tables)
        {
            if (t.ValueTType is TBean)
            {
                curSig = StructureSignature.ComputeForTable(t);
                break;
            }
        }
        if (string.IsNullOrEmpty(baseline.SignatureId) || curSig != baseline.SignatureId)
        {
            throw new InvalidOperationException(
                $"[增量导出已终止] L10N 结构变化：SignatureId 期望 {baseline.SignatureId} 实际 {curSig}。请重新执行基准导出。\n本次未产出任何 delta 文件。");
        }

        var languages = ctx.L10NLanguages;
        var keyFieldName = ctx.L10NTextKeyFieldName;
        var mergeOutput = EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily, "mergeOutput", false, "languageconfig");
        var perLang = BuildCurrent(ctx, languages, keyFieldName);

        var changed = new List<DeltaManifestEntry>();
        foreach (var lang in languages)
        {
            var cur = perLang.GetValueOrDefault(lang) ?? new Dictionary<object, string>();
            var curStr = new Dictionary<string, string>(cur.Count);
            foreach (var kv in cur)
            {
                curStr[kv.Key.ToString()] = kv.Value ?? "";
            }
            var curKeys = new HashSet<string>(curStr.Keys, StringComparer.Ordinal);

            var baseHashes = baseline.Languages.GetValueOrDefault(lang)?.Hashes; // 与 baseline.Keys 下标对齐
            var upserts = new List<KeyValuePair<string, string>>();
            var deletes = new List<string>();

            // 1) 遍历共享 Keys：当前无 -> delete；当前有但 hash 变 -> upsert
            for (int i = 0; i < baseline.Keys.Count && baseHashes != null; i++)
            {
                var k = baseline.Keys[i];
                if (!curKeys.Contains(k))
                {
                    deletes.Add(k);
                }
                else
                {
                    var md5 = FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(curStr[k] ?? ""));
                    if (baseHashes[i] != md5)
                    {
                        upserts.Add(new KeyValuePair<string, string>(k, curStr[k]));
                    }
                }
            }
            // 2) 当前有而基准 Keys 没有 -> upsert（新增 key）
            foreach (var kv in curStr)
            {
                if (baseHashes == null || !baseline.Keys.Contains(kv.Key))
                {
                    upserts.Add(kv);
                }
            }

            if (upserts.Count == 0 && deletes.Count == 0)
            {
                continue; // 该语言无变化不产 patch
            }

            var buf = new ByteBuf();
            PatchFormat.WriteMagic(buf, PatchFormat.MagicL10N);
            buf.WriteString(baseline.SignatureId);
            buf.WriteSize(upserts.Count);
            foreach (var kv in upserts)
            {
                buf.WriteString(kv.Key);
                buf.WriteString(kv.Value ?? "");
            }
            buf.WriteSize(deletes.Count);
            foreach (var k in deletes)
            {
                buf.WriteString(k);
            }

            var patchFile = $"{lang}/{mergeOutput}.patch.bytes";
            manifest.AddFile(new OutputFile { File = patchFile, Content = buf.CopyData() });
            changed.Add(new DeltaManifestEntry { Table = lang, UpsertCount = upserts.Count, DeleteCount = deletes.Count, PatchFile = patchFile });
        }

        // _l10n.delta.manifest
        var deltaManifest = new DeltaManifest { BaselineSignatureId = baseline.SignatureId, SidecarPath = sidecarPath, ChangedTables = changed };
        manifest.AddFile(new OutputFile
        {
            File = "_l10n.delta.manifest",
            Content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deltaManifest, new JsonSerializerOptions { WriteIndented = true })),
        });
    }

    /// <summary>
    /// 合并所有语言表 -> perLang[lang] = key -> value（镜像 ExportL10NMergedPerLanguage 的合并语义）。
    /// </summary>
    private static Dictionary<string, Dictionary<object, string>> BuildCurrent(GenerationContext ctx, IReadOnlyList<string> languages, string keyFieldName)
    {
        return L10NChecksumUtil.BuildPerLanguageMap(ctx, languages, keyFieldName);
    }
}