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
using Luban.Checksum;
using Luban.DataExporter.Builtin.Binary;
using Luban.DataTarget;
using Luban.Datas;
using Luban.Defs;
using Luban.L10N;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 组合基准导出器：一次 run 同时产出普通表 + 多语言 space。
/// 路由：不在任何 space 名单的表 -&gt; baseline-with-sidecar（现有逻辑，排除 space 表）；
/// indexMode space -&gt; int 字典合并 bin（v2：{outputDir}/{lang}/{outputFile}.bytes，server space 同款
/// 序列化路径，spec D3——数组格式已随 v2 退役）+ sidecar v2（含 id 注册表回写）；
/// 非 indexMode space -&gt; 现有逐表 int 字典 l10n-bin-split 逻辑（限定该 space 表，输出根 = space.OutputDir）；
/// checksum：普通表 checksumconfig.bytes 在输出根目录；每 space 在自己 outputDir 下输出
/// [表行 + per-language 行] 的 checksumconfig.bytes（表行在前，对齐合并前 legacy 输出形状）。
/// 未配置 l10n.spaces 时整体委托 baseline-with-sidecar，行为字节级等价。
/// </summary>
[DataExporter("omnibus-baseline")]
public class OmnibusBaselineExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var spaces = ctx.L10NSpaces;
        if (spaces.Count == 0)
        {
            // 未配置 spaces：整体走现有 baseline-with-sidecar（TableFilter=null），字节级等价
            new BaselineWithSidecarExporter().Handle(ctx, dataTarget, manifest);
            return;
        }

        var spaceTableNames = new HashSet<string>(spaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
        bool isSpaceTable(DefTable t) => spaceTableNames.Contains(t.Name) || spaceTableNames.Contains(t.FullName);

        // space 产物（数组 bin / string 字典 / per-space checksum）只有 bin DataTarget 有意义；
        // 非 bin 目标上 space 表不排除，按各自 DataTarget 默认逻辑整体导出。
        bool isBinary = dataTarget is BinaryDataTarget;

        // 1. 普通表（现有 baseline-with-sidecar，排除 space 表）
        var tableExporter = new BaselineWithSidecarExporter { TableFilter = isBinary ? isSpaceTable : null };
        tableExporter.Handle(ctx, dataTarget, manifest);

        if (!isBinary)
        {
            return;
        }

        foreach (var space in spaces)
        {
            if (space.Languages.Count == 0)
            {
                continue; // 无语言的 space 无产物
            }

            if (space.IndexMode)
            {
                // 2. int 字典合并 bin（v2，server space 同款序列化路径）+ sidecar v2（含 id 注册表回写）
                L10NBinarySplitDataExporter.ExportL10NMergedPerLanguage(ctx, L10NKeyIndexBuilder.MatchTables(ctx, space),
                    space.KeyFieldName, space.Languages, manifest, space.OutputFile, space.OutputDir);
                try
                {
                    L10NBaselineWithSidecarExporter.WriteIndexSpaceSidecar(ctx, space);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[omnibus-baseline] write sidecar for space '{space.Name}' failed: {e}");
                }
            }
            else
            {
                // 3. 逐表 int 字典（限定 space 表，输出根 = space.OutputDir）+ sidecar（与合并前 langServer 基准产物同构）
                WriteLegacySpace(ctx, dataTarget, space, manifest);
            }

            // 每 space 的 checksumconfig.bytes（[表行..., per-language 行...]），落在 space.OutputDir 下
            WriteSpaceChecksum(ctx, dataTarget, space, manifest);
        }
    }

    /// <summary>
    /// 非 indexMode space：复用 L10NBinarySplitDataExporter 的逐表 int 字典导出，
    /// TableFilter 反向选择（只留本 space 的表——L10NKeyIndexBuilder.MatchTables(space)，与 sidecar 同一表集合，
    /// 不含其它 space 的表），语言/key 字段/输出根前缀均换成 space 配置。
    /// 输出布局：{space.OutputDir}/{lang}/{OutputDataFile}.bytes（OutputDataFile 仍取表定义，与合并前一致）。
    /// sidecar：WriteLegacySpaceSidecar（int id 字典语义），写 space.SidecarPath。
    /// </summary>
    private static void WriteLegacySpace(GenerationContext ctx, IDataTarget dataTarget, L10NSpace space,
        OutputFileManifest manifest)
    {
        // 仅本 space 的表（与 WriteLegacySpaceSidecar 的 MatchTables 同源），防止其它 space 的同名语言列表混入本 space 输出目录
        var spaceTables = new HashSet<DefTable>(L10NKeyIndexBuilder.MatchTables(ctx, space));
        var exporter = new L10NBinarySplitDataExporter
        {
            TableFilter = t => !spaceTables.Contains(t), // 反向过滤：只保留本 space 的表
            LanguageScope = space.Languages,
            KeyFieldScope = space.KeyFieldName,
            OutputDirPrefix = space.OutputDir,
        };
        exporter.Handle(ctx, dataTarget, manifest);

        try
        {
            L10NBaselineWithSidecarExporter.WriteLegacySpaceSidecar(ctx, space, ctx.GetL10NSpaceLangStamps(space));
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[omnibus-baseline] write sidecar for space '{space.Name}' failed: {e}");
        }
    }

    /// <summary>
    /// 输出 {space.OutputDir}/checksumconfig.bytes：[表行..., per-language 行（TableName=语言名）...]。
    /// 语言行由 GenerationContext.BuildL10NSpaceChecksumRecords 在 LoadDatas 阶段生成（与 sidecar 共用同一份 stamp）；
    /// 表行在此补充（LoadDatas 后 table.Stamp/SignatureId/ContentHash 已定），与根 checksumconfig 的普通表行同构
    /// （TableName=表名，Stamp/SignatureId=表级 gating 值，与 sidecar 的 Tables 段同源），indexMode 与非 indexMode
    /// space 一视同仁 —— 对齐合并前 l10n-baseline-with-sidecar 的 space 输出形状（legacy 690B = 1 表行 + 14 语言行）。
    /// </summary>
    private static void WriteSpaceChecksum(GenerationContext ctx, IDataTarget dataTarget, L10NSpace space,
        OutputFileManifest manifest)
    {
        if (!ctx.L10NSpaceChecksumRecords.TryGetValue(space.Name, out var records) || records.Count == 0)
        {
            return;
        }
        DefTable checksumTable = null;
        foreach (var t in ctx.ExportTables)
        {
            if (t.Name == ChecksumTableBuilder.ChecksumTableName)
            {
                checksumTable = t;
                break;
            }
        }
        if (checksumTable == null)
        {
            return;
        }

        // 表行在前：本 space 名单表逐张一行（跳过 ChecksumConfig 自身与无数据表，与根行/sidecar Tables 段同一跳过规则）
        var allRecords = new List<Record>(records.Count + 4);
        foreach (var table in L10NKeyIndexBuilder.MatchTables(ctx, space))
        {
            if (table.Name == ChecksumTableBuilder.ChecksumTableName || string.IsNullOrEmpty(table.ContentHash))
            {
                continue;
            }
            allRecords.Add(ChecksumTableBuilder.CreateChecksumRecord(checksumTable, table.Name, table.Stamp,
                table.SignatureId ?? ""));
        }
        allRecords.AddRange(records);

        var file = dataTarget.ExportTable(checksumTable, allRecords);
        if (file == null)
        {
            return;
        }
        manifest.AddFile(new OutputFile
        {
            File = Path.Combine(space.OutputDir ?? "", file.File),
            Content = file.Content,
            Encoding = file.Encoding,
        });
    }
}
