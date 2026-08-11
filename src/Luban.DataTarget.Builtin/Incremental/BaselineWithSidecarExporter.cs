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
using Luban.DataExporter.Builtin.Binary;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Incremental;
using Luban.Serialization;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 基准导出 + 写基准 sidecar（普通表）。
/// 包装 TagSplitDataExporter：先正常 tag-split 产 .bytes，再用 BinaryDataVisitor 行字节
/// 算 per-row MD5（按目标 group 过滤字段），写 _baseline.{target}.sidecar.json。
/// sidecar 是工具内部产物，不 ship，供增量导出器做结构 gate + 行级 diff。
/// </summary>
[DataExporter("baseline-with-sidecar")]
public class BaselineWithSidecarExporter : TagSplitDataExporter
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 正常 tag-split 导出（产 .bytes）
        base.Handle(ctx, dataTarget, manifest);

        // 2. 写 sidecar（失败不阻断基准导出）
        try
        {
            WriteSidecar(ctx);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[baseline-with-sidecar] write sidecar failed: {e}");
        }
    }

    private void WriteSidecar(GenerationContext ctx)
    {
        // 从 -x incremental.sidecarPath=... 读 sidecar 路径；未配置则跳过
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sidecar = new BaselineSidecar { Target = ctx.Target.Name };
        foreach (var table in ctx.Tables)
        {
            if (table.Name == ChecksumTableBuilder.ChecksumTableName)
            {
                continue; // 虚拟 checksum 表不进 sidecar（数据 MD5 每次都会变，无增量意义）
            }

            var records = ctx.GetTableExportDataList(table);
            if (records == null || records.Count == 0)
            {
                continue;
            }

            var entry = new TableSidecarEntry
            {
                SignatureId = table.SignatureId,
                Mode = table.Mode.ToString(),
                PrimaryKeyIndex = table.Index ?? "",
                RowCount = records.Count,
                ContentHash = table.ContentHash,
                Stamp = table.Stamp,
                RowHashes = new Dictionary<string, string>(records.Count),
            };
            foreach (var rec in records)
            {
                var buf = new ByteBuf();
                rec.Data.Apply(BinaryDataVisitor.Ins, buf);
                entry.RowHashes[ExtractKey(table, rec)] = FileUtil.CalcMD5(buf.CopyData());
            }
            sidecar.Tables[table.FullName] = entry;
        }
        BaselineSidecarIO.Save(path, sidecar);
    }

    /// <summary>
    /// 行键 = 主索引第一个字段（IndexFieldIdIndex），与客户端 dataMap 的 key 一致
    /// （MAP 表按主键；LIST 表按主索引首字段，如 Building 按 id、MapMarch 按 Action）。
    /// 无有效索引时退化为记录序号（AutoIndex，供 ONE/无主键表兜底）。
    /// 与增量导出器共享同一实现，保证 sidecar 与 diff 的 key 一致。
    /// </summary>
    internal static string ExtractKey(DefTable table, Record rec)
    {
        var idx = table.IndexFieldIdIndex;
        if (idx >= 0 && idx < rec.Data.Fields.Count)
        {
            return rec.Data.Fields[idx].ToString();
        }
        return rec.AutoIndex.ToString();
    }
}