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
using Luban.DataTarget;
using Luban.Defs;
using Luban.Incremental;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 组合增量导出器：普通表 DLP1（排除 space 表）+ indexMode space LLP2（全 id）+ id 注册表回写。
/// 产出单份 _delta.manifest（普通表条目 + 语言条目混排，语言条目 Table=语言名）。
/// 路由与 omnibus-baseline 对称：
/// - 不在任何 space 名单的表 -> 现有 incremental 的 DLP1 行 diff（结构 gate 与 diff 都按过滤后的表集合跑）；
/// - indexMode space -> IncrementalL10NDataExporter.HandleSpace（LLP2 全 id + 注册表回写，Stamp 用 per-space 口径，
///   与 space sidecar / space checksumconfig 语言行同值）；
/// - 非 indexMode（legacy/server）space -> 无增量导出（与合并前 langServer 现状一致：legacy space 只有基准产物）。
/// 未配置 l10n.spaces 时整体委托 incremental，行为字节级等价。
/// </summary>
[DataExporter("omnibus-incremental")]
public class OmnibusIncrementalExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var spaces = ctx.L10NSpaces;
        if (spaces.Count == 0)
        {
            // 未配置 spaces：整体走现有 incremental（TableFilter=null），字节级等价
            new IncrementalDataExporter().Handle(ctx, dataTarget, manifest);
            return;
        }

        var spaceTableNames = new HashSet<string>(spaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
        bool isSpaceTable(DefTable t) => spaceTableNames.Contains(t.Name) || spaceTableNames.Contains(t.FullName);

        // 1. 普通表增量（现有 DLP1，排除全部 space 表——space 表不在普通 baseline sidecar 内，由 space 逻辑接管；
        //    增量 patch 本身就是字节格式，不随 dataTarget 切换，故不像 omnibus-baseline 那样按 isBinary 放开过滤）
        var tableExporter = new IncrementalDataExporter { TableFilter = isSpaceTable };
        var (entries, sidecarPath) = tableExporter.CollectDelta(ctx, manifest);

        // 2. indexMode space LLP2 全 id patch（内含 id 注册表回写）；legacy/server space 无增量
        foreach (var space in spaces.Where(s => s.IndexMode))
        {
            IncrementalL10NDataExporter.HandleSpace(ctx, space, manifest, entries, ctx.GetL10NSpaceLangStamps(space));
        }

        // 3. 单份合并 _delta.manifest：普通表条目 + 语言条目（Table=语言名），SidecarPath 取普通表 sidecar
        IncrementalDataExporter.WriteDeltaManifestFile(manifest, new DeltaManifest { SidecarPath = sidecarPath, ChangedTables = entries });
    }
}
