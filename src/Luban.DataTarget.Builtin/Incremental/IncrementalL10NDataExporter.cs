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
using Luban.Defs;
using Luban.Incremental;
using Luban.L10N;
using Luban.Serialization;
using Luban.Types;
using Luban.TypeVisitors;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// L10N 增量导出器。
/// 读 L10N sidecar -> 结构 gate（Language bean SignatureId）-> per-语言 id->value 行级 diff -> 出 patch + _l10n.delta.manifest。
/// v2（spec 2026-08-22）：语言键 = 显式 int id（语言表 id 列），LLP2 是唯一增量路径
/// （diff 单位 = (语言, id)；新增 id -> 所有语言各 upsert；删除 id -> 所有语言各 delete；改某语言文案 -> 只该语言 upsert）；
/// 旧 LLP1（string key diff）随 v2 退役——未配置 indexMode space 时直接报错指路。
/// 基线冻结：sidecar 只读不写——增量永远以这份基准快照 diff（累计对基准，补丁 C 相对基线 A 而非补丁 B）。
/// </summary>
[DataExporter("incremental-l10n-bin-split")]
public class IncrementalL10NDataExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // v2：只有 indexMode space 路径（LLP2，id 语义）；旧 string-key LLP1 路径已随显式 int id 退役
        if (!ctx.L10NSpaces.Any(s => s.IndexMode))
        {
            throw new InvalidOperationException(
                "[incremental-l10n] v2 语言增量需要 indexMode space：语言键已改为显式 int id（语言表 id 列），旧 string-key LLP1 路径已退役。" +
                "请配置 l10n.*.indexMode=true（如 l10n.main.indexMode）或改用 omnibus-incremental。");
        }

        var spaceSidecarPath = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        HandleIndexMode(ctx, manifest, spaceSidecarPath);
    }

    /// <summary>
    /// indexMode space 路径：LLP2 全 id 语言增量 patch。
    /// diff 基准 = sidecar 基准快照（Keys/Languages.Hashes，基准时刻活 id 的紧凑视图），语义为"累计对基准"：
    /// 基准快照内的 id 按基准 hash 比对（变化 upsert / 消失 delete）；
    /// 不在基准快照的活 id（基准后追加）恒 upsert。
    /// 基线冻结：sidecar 完全只读，不回写任何字段。
    /// </summary>
    private static void HandleIndexMode(GenerationContext ctx, OutputFileManifest manifest, string sidecarPath)
    {
        if (string.IsNullOrEmpty(sidecarPath))
        {
            throw new InvalidOperationException("[incremental-l10n] 未配置 incremental.sidecarPath，无法 diff。请先跑基准导出。");
        }

        var changed = new List<DeltaManifestEntry>();
        // per-language stamp：旧入口沿用全表口径（omnibus-incremental 调 HandleSpace 时按 space 口径计算）
        var langStamps = ctx.GetL10NLangStamps();
        string firstSig = "";
        string firstSidecarPath = "";

        foreach (var space in ctx.L10NSpaces.Where(s => s.IndexMode))
        {
            var sig = HandleSpace(ctx, space, manifest, changed, langStamps);
            if (string.IsNullOrEmpty(firstSig))
            {
                firstSig = sig;
                firstSidecarPath = space.SidecarPath;
            }
        }

        // _l10n.delta.manifest（本导出器单独使用时仍写；omnibus-incremental 调 HandleSpace 后统一与普通表 _delta.manifest 合并）
        var deltaManifest = new DeltaManifest { BaselineSignatureId = firstSig, SidecarPath = firstSidecarPath, ChangedTables = changed };
        manifest.AddFile(new OutputFile
        {
            File = "_l10n.delta.manifest",
            Content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deltaManifest, new JsonSerializerOptions { WriteIndented = true })),
        });
    }

    /// <summary>
    /// 单个 indexMode space 的 LLP2 增量（HandleIndexMode 的循环体抽出，供 omnibus-incremental 按 space 复用）：
    /// 结构 gate -> per-语言 (id, value) 行级 diff -> patch 文件入 manifest、条目追加 entries（Table=语言名，
    /// Stamp 取 langStamps——单独使用传全表口径 GetL10NLangStamps()，omnibus 传 per-space 口径 GetL10NSpaceLangStamps(space)，
    /// 后者与 space sidecar / space checksumconfig 语言行同值）。基线冻结：sidecar 只读不写，不产生任何副作用。
    /// 返回该 space 通过 gate 的基准 SignatureId。
    /// v2：LLP2 字段为显式 int 语言 id（不再是数组注册表位置）；v3（spec 2026-08-25）头部加字符串表，
    /// 布局 magic+sig+[字符串表]+varint upsertCount+(int id, WriteSize(valIndex))*+varint delCount+int id*；
    /// id 即键，无需下标换算，基准 id 缺失于当前时
    /// 自然落 delete 分支（基准侧损伤自愈为"该 id 已删除"）。
    /// </summary>
    internal static string HandleSpace(GenerationContext ctx, L10NSpace space, OutputFileManifest manifest,
        List<DeltaManifestEntry> entries, Dictionary<string, (string ContentHash, long Stamp)> langStamps)
    {
        if (string.IsNullOrEmpty(space.SidecarPath))
        {
            throw new InvalidOperationException(
                $"[incremental-l10n] space '{space.Name}' 未配置 l10n.{space.Name}.sidecar，index 模式增量 diff 需要基准 sidecar。");
        }

        var baseline = BaselineSidecarIO.LoadL10N(space.SidecarPath);
        var spaceTables = L10NKeyIndexBuilder.MatchTables(ctx, space);

        // 结构 gate：space 内全语言共享一个 SignatureId
        var sigTable = spaceTables.FirstOrDefault(t => t.ValueTType is TBean);
        if (sigTable == null)
        {
            string tableList = string.Join(",", space.Tables);
            throw new InvalidOperationException(
                $"[incremental-l10n] space '{space.Name}' 的表名单（{tableList}）未命中任何 bean 结构语言表，无法计算结构签名。");
        }
        string curSig = StructureSignature.ComputeForTable(sigTable);
        if (string.IsNullOrEmpty(baseline.SignatureId) || curSig != baseline.SignatureId)
        {
            throw new InvalidOperationException(
                $"[增量导出已终止] L10N space '{space.Name}' 结构变化：SignatureId 期望 {baseline.SignatureId} 实际 {curSig}。请重新执行基准导出。\n本次未产出任何 delta 文件。");
        }

        // 当前值：space 名单表合并 -> lang -> (id, value)（BuildPerLanguageMap 的 space 限定版）
        var perLang = L10NChecksumUtil.BuildPerLanguageMap(ctx, space.Languages, space.KeyFieldName, spaceTables);

        // v2：id 即键，无注册表位置换算。当前活 id 集直接来自语言表合并结果（无墓碑——
        // 基线冻结语义下 diff 只需当前数据 vs 基准快照两方）。
        var baseKeySet = new HashSet<int>(baseline.Keys);

        foreach (var lang in space.Languages)
        {
            var cur = perLang.GetValueOrDefault(lang) ?? new Dictionary<object, string>();
            var curInt = new Dictionary<int, string>(cur.Count);
            foreach (var kv in cur)
            {
                curInt[L10NChecksumUtil.ToIntId(kv.Key)] = kv.Value ?? "";
            }
            var baseHashes = baseline.Languages.GetValueOrDefault(lang)?.Hashes; // 与 baseline.Keys 下标对齐
            int baseHashCount = baseHashes?.Count ?? 0;
            var upserts = new List<(int Id, string Value)>();
            var deletes = new List<int>();

            // 1) 基准快照内的 id：当前无 -> delete；当前有但 hash 变 -> upsert
            for (int i = 0; i < baseline.Keys.Count && i < baseHashCount; i++)
            {
                var k = baseline.Keys[i];
                if (!curInt.TryGetValue(k, out var curVal))
                {
                    deletes.Add(k);
                    continue;
                }
                var md5 = FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(curVal ?? ""));
                if (baseHashes[i] != md5)
                {
                    upserts.Add((k, curVal ?? ""));
                }
            }
            // 2) 不在基准快照的活 id（基准后新增）：全部 upsert（id 在语言表内，无需注册表记账）。
            //    baseHashCount == 0（该语言不在基准快照里）时连基准 id 一起 upsert，对齐"新语言全量"语义。
            foreach (var kv in curInt)
            {
                if (baseHashCount > 0 && baseKeySet.Contains(kv.Key))
                {
                    continue;
                }
                upserts.Add((kv.Key, kv.Value ?? ""));
            }

            if (upserts.Count == 0 && deletes.Count == 0)
            {
                continue; // 该语言无变化不产 patch
            }

            var buf = new ByteBuf();
            PatchFormat.WriteMagic(buf, PatchFormat.MagicL10N2);
            buf.WriteString(baseline.SignatureId);
            // 字符串表：upsert 的 val 全部入表（LLP2 新布局，spec 2026-08-25 §4.4）
            var builder = new StringTableBuilder();
            foreach (var upsert in upserts)
            {
                builder.GetOrAddIndex(upsert.Value);
            }
            builder.Write(buf);
            buf.WriteSize(upserts.Count);
            foreach (var upsert in upserts)
            {
                buf.WriteInt(upsert.Id);
                buf.WriteSize(builder.GetOrAddIndex(upsert.Value));
            }
            buf.WriteSize(deletes.Count);
            foreach (var id in deletes)
            {
                buf.WriteInt(id);
            }

            string patchFile = $"{space.OutputDir}/{lang}/{space.OutputFile}.patch.bytes";
            manifest.AddFile(new OutputFile { File = patchFile, Content = buf.CopyData() });
            entries.Add(new DeltaManifestEntry
            {
                Table = lang,
                UpsertCount = upserts.Count,
                DeleteCount = deletes.Count,
                PatchFile = patchFile,
                Stamp = langStamps.GetValueOrDefault(lang).Stamp,
            });
        }

        // 基线冻结：sidecar 完全只读，增量 run 不回写任何字段——
        // 后续增量始终以同一份基准快照 diff（累计对基准语义，补丁 C 相对基线 A 而非补丁 B）。
        return baseline.SignatureId;
    }
}