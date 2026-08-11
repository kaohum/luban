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
using Luban.Checksum;
using Luban.DataExporter.Builtin.Binary;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Incremental;
using Luban.Serialization;
using Luban.TypeVisitors;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 增量导出器（普通表）。
/// 读基准 sidecar -> 结构 gate（SignatureId 全表扫，任何不一致整批中止）-> 行级 diff（按主键）-> 出 DLP1 patch + _delta.manifest。
/// delta 永远是"基准->当前"累计 diff，服务器只留最新一份。
/// </summary>
[DataExporter("incremental")]
public class IncrementalDataExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var sidecarPath = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(sidecarPath))
        {
            throw new InvalidOperationException("[incremental] 未配置 incremental.sidecarPath，无法 diff。请先跑基准导出。");
        }

        var baseline = BaselineSidecarIO.Load(sidecarPath);
        if (baseline.Target != ctx.Target.Name)
        {
            throw new InvalidOperationException($"[incremental] sidecar target 不匹配：sidecar={baseline.Target}，当前={ctx.Target.Name}");
        }

        // 第一遍：结构 gate（全表扫，收集所有不一致，一次性报全）
        var offenders = new List<(string Table, string Reason)>();
        foreach (var table in ctx.Tables)
        {
            if (table.Name == ChecksumTableBuilder.ChecksumTableName)
            {
                continue;
            }

            var curSig = StructureSignature.ComputeForTable(table);
            if (!baseline.Tables.TryGetValue(table.FullName, out var baseEntry))
            {
                offenders.Add((table.FullName, "基准中不存在（新增表，需新客户端代码）"));
                continue;
            }
            if (curSig != baseEntry.SignatureId)
            {
                offenders.Add((table.FullName, $"SignatureId 期望 {baseEntry.SignatureId} 实际 {curSig}（结构变化）"));
            }
        }
        foreach (var kv in baseline.Tables)
        {
            if (!ctx.Tables.Any(t => t.FullName == kv.Key))
            {
                offenders.Add((kv.Key, "当前已移除（删除表）"));
            }
        }

        if (offenders.Count > 0)
        {
            var msg = new StringBuilder();
            msg.AppendLine("[增量导出已终止] 检测到结构变化，无法在旧基准上叠加增量。请重新执行基准导出（会刷新 sidecar）。");
            foreach (var o in offenders)
            {
                msg.AppendLine($"  - {o.Table} : {o.Reason}");
            }
            msg.AppendLine("本次未产出任何 delta 文件。");
            throw new InvalidOperationException(msg.ToString());
        }

        // 第二遍：行 diff（仅结构全一致时）
        var changedTables = new List<DeltaManifestEntry>();
        foreach (var table in ctx.Tables)
        {
            if (table.Name == ChecksumTableBuilder.ChecksumTableName)
            {
                continue;
            }

            var baseEntry = baseline.Tables[table.FullName];
            if (!IsStableKey(table, baseEntry))
            {
                // 无稳定行键（联合索引首字段不唯一，或 ONE 无索引退化）：不进增量，客户端走基准全量
                Console.WriteLine($"[incremental] 跳过 {table.FullName}（无稳定行键，进基准全量）");
                continue;
            }

            var records = ctx.GetTableExportDataList(table);
            var curHashes = new Dictionary<string, (string Hash, Record Rec)>(records.Count);
            foreach (var rec in records)
            {
                var buf = new ByteBuf();
                rec.Data.Apply(BinaryDataVisitor.Ins, buf);
                curHashes[BaselineWithSidecarExporter.ExtractKey(table, rec)] = (FileUtil.CalcMD5(buf.CopyData()), rec);
            }

            var upserts = new List<Record>();
            var deletes = new List<string>();
            foreach (var kv in curHashes)
            {
                if (!baseEntry.RowHashes.TryGetValue(kv.Key, out var oldHash) || oldHash != kv.Value.Hash)
                {
                    upserts.Add(kv.Value.Rec); // 新 key 或行内容变化
                }
            }
            foreach (var kv in baseEntry.RowHashes)
            {
                if (!curHashes.ContainsKey(kv.Key))
                {
                    deletes.Add(kv.Key); // 基准有、当前无 -> 删除
                }
            }

            if (upserts.Count == 0 && deletes.Count == 0)
            {
                continue; // 无变化不产 patch
            }

            var file = WritePatch(table, baseEntry.SignatureId, upserts, deletes);
            manifest.AddFile(file);
            changedTables.Add(new DeltaManifestEntry
            {
                Table = table.FullName,
                UpsertCount = upserts.Count,
                DeleteCount = deletes.Count,
                PatchFile = file.File,
                Stamp = table.Stamp,
            });
        }

        // _delta.manifest（服务器侧 patch 索引）
        var deltaManifest = new DeltaManifest { SidecarPath = sidecarPath, ChangedTables = changedTables };
        manifest.AddFile(new OutputFile
        {
            File = "_delta.manifest",
            Content = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(deltaManifest, new JsonSerializerOptions { WriteIndented = true })),
        });
    }

    /// <summary>
    /// 该表是否具有稳定的行级键，可做行级 diff：
    /// - ONE 表：单记录，AutoIndex=0 稳定，放行；
    /// - 其余：主索引首字段有效 且 基准行键无碰撞（rowHashes 数 == 行数）。
    /// 联合索引首字段不唯一的表（如 SceneJumpType/Action+Target）行键不可靠，不进增量。
    /// </summary>
    private static bool IsStableKey(DefTable table, TableSidecarEntry entry)
    {
        if (table.IsSingletonTable)
        {
            return true;
        }
        return table.IndexFieldIdIndex >= 0 && entry.RowHashes.Count == entry.RowCount;
    }

    /// <summary>
    /// 写 DLP1 patch：magic + signatureId + upsert 行字节 + delete 主键。
    /// upsert 行字节与全表 .bytes 一致（BinaryDataVisitor 按 group 过滤），客户端复用现有反序列化。
    /// </summary>
    private static OutputFile WritePatch(DefTable table, string signatureId, List<Record> upserts, List<string> deletes)
    {
        var buf = new ByteBuf();
        PatchFormat.WriteMagic(buf, PatchFormat.MagicTable);
        buf.WriteString(signatureId);
        buf.WriteSize(upserts.Count);
        foreach (var rec in upserts)
        {
            rec.Data.Apply(BinaryDataVisitor.Ins, buf);
        }
        buf.WriteSize(deletes.Count);
        foreach (var k in deletes)
        {
            buf.WriteString(k);
        }
        return new OutputFile { File = $"{table.OutputDataFile}.patch.bytes", Content = buf.CopyData() };
    }
}