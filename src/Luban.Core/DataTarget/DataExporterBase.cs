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
using System.Linq;
using Luban.Defs;

namespace Luban.DataTarget;

public abstract class DataExporterBase : IDataExporter
{
    public const string FamilyPrefix = "dataExporter";

    /// <summary>
    /// 组合导出（omnibus-baseline）时排除部分表（返回 true = 跳过该表，由组合导出器另行接管）；
    /// null = 不过滤（默认，行为与原先完全一致）。
    /// </summary>
    public Func<DefTable, bool> TableFilter { get; set; }

    /// <summary>
    /// 解析本次导出的表集合（ExportAllRecords ? 全部表 : 导出表），并应用 TableFilter。
    /// </summary>
    protected List<DefTable> SelectTables(GenerationContext ctx, IDataTarget dataTarget)
    {
        var tables = dataTarget.ExportAllRecords ? ctx.Tables : ctx.ExportTables;
        return TableFilter != null ? tables.Where(t => !TableFilter(t)).ToList() : tables;
    }

    public virtual void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        List<DefTable> tables = SelectTables(ctx, dataTarget);
        switch (dataTarget.AggregationType)
        {
            case AggregationType.Table:
            {
                var tasks = tables.Select(table => Task.Run(() =>
                {
                    manifest.AddFile(dataTarget.ExportTable(table, ctx.GetTableExportDataList(table)));
                })).ToArray();
                Task.WaitAll(tasks);
                break;
            }
            case AggregationType.Tables:
            {
                // 注意：此路径历史上固定用 ctx.ExportTables（不随 ExportAllRecords 切换），保持不变，仅叠加过滤
                var allTables = ctx.ExportTables;
                if (TableFilter != null)
                {
                    allTables = allTables.Where(t => !TableFilter(t)).ToList();
                }
                manifest.AddFile(dataTarget.ExportTables(allTables));
                break;
            }
            case AggregationType.Record:
            {
                var tasks = new List<Task>();
                foreach (var table in tables)
                {
                    foreach (var record in ctx.GetTableExportDataList(table))
                    {
                        tasks.Add(Task.Run(() =>
                        {
                            manifest.AddFile(dataTarget.ExportRecord(table, record));
                        }));
                    }
                }
                Task.WaitAll(tasks.ToArray());
                break;
            }
            case AggregationType.Other:
            {
                ExportCustom(tables, manifest, dataTarget);
                break;
            }
        }
    }

    protected virtual void ExportCustom(List<DefTable> tables, OutputFileManifest manifest, IDataTarget dataTarget)
    {

    }
}
