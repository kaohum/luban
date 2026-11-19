// 客户端 omnibus 复合导出：在原 omnibus-baseline（compact 基础表 + 多语言 space + 增量 sidecar）之上，
// 同一次 Luban 调用内把普通表二进制覆盖为 bin-row v3 行表（运行时唯一读取格式），并可把 compact 版本
// 暂存到独立目录（服务端 classpath 等仍消费 compact 的链路使用）。
// 列编码方案经 BinRowPlanAnalyzer 与 cs-bin 代码生成共享，无 sidecar 文件、无跨调用顺序约束。

using Luban;
using Luban.DataExporter.Builtin.Binary;
using Luban.DataExporter.Builtin.Incremental;
using Luban.DataTarget;
using Luban.Defs;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace Luban.DataExporter.Builtin.Binary;

[DataExporter("omnibus-baseline-binrow")]
public class OmnibusBaselineBinRowExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 原 omnibus-baseline 全量行为（compact 基础表 → {outDir}/basic、语言 space、增量 sidecar）
        new OmnibusBaselineExporter().Handle(ctx, dataTarget, manifest);

        // 注意：manifest 落盘在 Handle 之后，磁盘上此时还没有 compact 文件——
        // 暂存与覆盖都基于 manifest 条目（追加在 compact 条目之后，Save 顺序保证行表最终胜出）。

        // 2. compact 暂存（可选）：供服务端 classpath 等 compact 消费方（此刻 basic/ 条目全为 compact 产物）
        string stageDir = EnvManager.Current.GetOptionOrDefault("omnibus-baseline-binrow", "compactStageDir", true, "");
        var compactBasic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.DataFiles)
        {
            if (f.File == null) continue;
            var norm = f.File.Replace('\\', '/');
            if (norm.StartsWith("basic/", StringComparison.OrdinalIgnoreCase))
            {
                compactBasic.Add(norm);
            }
        }
        if (!string.IsNullOrWhiteSpace(stageDir))
        {
            if (Directory.Exists(stageDir)) Directory.Delete(stageDir, true);
            Directory.CreateDirectory(stageDir);
            int staged = 0;
            foreach (var f in manifest.DataFiles)
            {
                if (f.File == null || !f.File.Replace('\\', '/').StartsWith("basic/", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
                File.WriteAllBytes(Path.Combine(stageDir, Path.GetFileName(f.File)), f.GetContentBytes());
                staged++;
            }
            Console.WriteLine($"[omnibus-baseline-binrow] compact 暂存 {staged} 文件 → {stageDir}");
        }

        // 3. 行表覆盖：追加 basic/{表}.bytes 的 bin-row v3 条目（Save 序在 compact 之后，最终生效）
        var spaceTables = new HashSet<string>(ctx.L10NSpaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
        var rowTarget = new BinaryRowDataTarget();
        int written = 0;
        foreach (var table in ctx.ExportTables)
        {
            if (spaceTables.Contains(table.Name) || spaceTables.Contains(table.FullName))
            {
                continue;
            }
            var info = ctx.GetTableDataInfo(table);
            var records = info?.FinalRecords;
            if (records == null || records.Count == 0)
            {
                Console.WriteLine($"[omnibus-baseline-binrow] skip(no records): {table.FullName}");
                continue;
            }
            var of = rowTarget.ExportTable(table, records);
            var target = "basic/" + of.File;
            if (!compactBasic.Contains(target))
            {
                // compact 基础条目缺失（tag 目录名漂移/导出集不一致）——静默覆盖会产出混合格式，直接 fail-loud
                throw new Exception($"[omnibus-baseline-binrow] 缺少 compact 条目: {target}（basic 目录布局与预期不符）");
            }
            manifest.AddFile(new OutputFile
            {
                File = target,
                Content = of.GetContentBytes(),
            });
            written++;
        }
        Console.WriteLine($"[omnibus-baseline-binrow] bin-row v3 覆盖 {written} 张普通表（manifest 追加）");
    }
}