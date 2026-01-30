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

using System.Text;
using Luban.DataTarget;
using Luban.Defs;

namespace Luban.DataExporter.Builtin.Csv;

[DataTarget("csv")]
public class CsvDataTarget : DataTargetBase
{
    protected override string DefaultOutputFileExt => "csv";

    public static string Delimiter => EnvManager.Current.GetOptionOrDefault("csv", "delimiter", true, ",");
    
    public static bool IncludeHeader => EnvManager.Current.GetBoolOptionOrDefault("csv", "header", true, true);

    public override OutputFile ExportTable(DefTable table, List<Record> records)
    {
        var sb = new StringBuilder();
        var delimiter = Delimiter;
        
        // 获取表的字段定义
        var valueType = table.ValueTType;
        var defBean = valueType.DefBean;
        var fields = defBean.HierarchyFields;

        // 写入表头
        if (IncludeHeader)
        {
            for (int i = 0; i < fields.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                sb.Append(EscapeCsvField(fields[i].Name));
            }
            sb.AppendLine();
        }

        // 写入数据行
        foreach (var record in records)
        {
            var bean = record.Data;
            for (int i = 0; i < bean.Fields.Count; i++)
            {
                if (i > 0) sb.Append(delimiter);
                
                var field = bean.Fields[i];
                if (field != null)
                {
                    var value = field.Apply(CsvDataVisitor.Ins);
                    sb.Append(EscapeCsvField(value));
                }
            }
            sb.AppendLine();
        }

        return CreateOutputFile($"{table.OutputDataFile}.{OutputFileExt}", sb.ToString());
    }

    private static string EscapeCsvField(string field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return "";
        }

        // 如果字段包含逗号、引号、换行符，需要用引号包裹并转义引号
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r'))
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }
}
