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

using Luban.DataTarget;
using Luban.Defs;
using Luban.Utils;

namespace Luban.DataExporter.Builtin;

/// <summary>
/// 按 tag 拆分导出数据：
/// - 命令行使用 -i dev,test 时，会导出 dev/xxx、test/xxx 目录
/// - 未指定 -i 时保持默认行为
/// - 仅影响数据导出，不影响代码生成
/// 使用方式：在 conf 中配置 dataExporter = "tag-split"
/// </summary>
[DataExporter("tag-split")]
public class TagSplitDataExporter : DataExporterBase
{
    private static List<string> GetAllTagsForExport(GenerationContext ctx)
    {
        // GenerationContext.AllTags 已经包含 (IncludeTags + 默认 _base_)，
        // 如果调用端没有传 -i，则 AllTags 为空列表。
        if (ctx.AllTags != null && ctx.AllTags.Count > 0)
        {
            return ctx.AllTags;
        }
        return new List<string>();
    }

    private static bool RecordHasTag(Record rec, string tag)
    {
        if (rec.Tags == null || rec.Tags.Count == 0)
        {
            return false;
        }
        return rec.Tags.Contains(tag);
    }

    private static OutputFile WrapOutputFileWithTagDir(OutputFile file, string tag)
    {
        var newPath = Path.Combine(tag, file.File);
        return new OutputFile
        {
            File = newPath,
            Content = file.Content,
            Encoding = file.Encoding,
        };
    }

    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var tags = GetAllTagsForExport(ctx);
        // 没有 tag（未使用 -i）时，完全复用默认逻辑，行为与原来一致
        if (tags.Count == 0)
        {
            base.Handle(ctx, dataTarget, manifest);
            return;
        }

        var tables = dataTarget.ExportAllRecords ? ctx.Tables : ctx.ExportTables;

        switch (dataTarget.AggregationType)
        {
            case AggregationType.Table:
            {
                foreach (var tag in tags)
                {
                    var tasks = tables.Select(table => Task.Run(() =>
                    {
                        var allRecords = ctx.GetTableExportDataList(table);
                        var tagRecords = allRecords.Where(r => RecordHasTag(r, tag)).ToList();
                        if (tagRecords.Count == 0)
                        {
                            return;
                        }

                        var file = dataTarget.ExportTable(table, tagRecords);
                        if (file != null)
                        {
                            manifest.AddFile(WrapOutputFileWithTagDir(file, tag));
                        }
                    })).ToArray();

                    Task.WaitAll(tasks);
                }
                break;
            }
            case AggregationType.Record:
            {
                foreach (var tag in tags)
                {
                    var tasks = new List<Task>();
                    foreach (var table in tables)
                    {
                        var allRecords = ctx.GetTableExportDataList(table);
                        foreach (var record in allRecords)
                        {
                            if (!RecordHasTag(record, tag))
                            {
                                continue;
                            }

                            tasks.Add(Task.Run(() =>
                            {
                                var file = dataTarget.ExportRecord(table, record);
                                if (file != null)
                                {
                                    manifest.AddFile(WrapOutputFileWithTagDir(file, tag));
                                }
                            }));
                        }
                    }

                    Task.WaitAll(tasks.ToArray());
                }
                break;
            }
            default:
            {
                // 其它聚合方式（如 L10N text-list）维持默认行为
                base.Handle(ctx, dataTarget, manifest);
                break;
            }
        }
    }
}


