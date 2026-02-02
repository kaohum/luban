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
using System.Collections.Concurrent;

namespace Luban.CsvSource;

/// <summary>
/// CSV源文件导出器，用于将原始数据源转换为CSV格式的中间产物
/// </summary>
public class CsvSourceExporter
{
    private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

    private readonly ConcurrentBag<CsvSourceData> _csvSourceDatas = new();

    public static CsvSourceExporter Instance { get; } = new();

    /// <summary>
    /// 记录CSV源数据
    /// </summary>
    public void RecordCsvSourceData(CsvSourceData data)
    {
        _csvSourceDatas.Add(data);
    }

    /// <summary>
    /// 导出所有CSV源文件
    /// </summary>
    /// <param name="outputDir">输出目录</param>
    public void ExportAll(string outputDir)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            s_logger.Debug("csv source output dir is not set, skip csv source export");
            return;
        }

        if (_csvSourceDatas.IsEmpty)
        {
            s_logger.Debug("no csv source data to export");
            return;
        }

        s_logger.Info("开始导出CSV源文件到: {}", outputDir);

        // 确保输出目录存在
        if (!Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
            s_logger.Debug("创建CSV源文件输出目录: {}", outputDir);
        }

        // 按文件分组导出
        var groupedData = _csvSourceDatas.GroupBy(d => d.SourceFile);
        
        foreach (var group in groupedData)
        {
            try
            {
                ExportFile(outputDir, group.Key, group.ToList());
            }
            catch (Exception ex)
            {
                s_logger.Error(ex, "导出CSV源文件失败: {}", group.Key);
            }
        }

        s_logger.Info("CSV源文件导出完成，共导出 {} 个文件", groupedData.Count());
    }

    /// <summary>
    /// 导出单个文件的CSV源数据
    /// </summary>
    private void ExportFile(string outputDir, string sourceFile, List<CsvSourceData> datas)
    {
        // 生成输出文件名
        string fileName = Path.GetFileNameWithoutExtension(sourceFile);
        string outputPath = Path.Combine(outputDir, $"{fileName}.csv");

        s_logger.Debug("导出CSV源文件: {} -> {}", sourceFile, outputPath);

        using var writer = new StreamWriter(outputPath, false, Encoding.UTF8);

        foreach (var data in datas)
        {
            // 写入Sheet名称（如果有）
            if (!string.IsNullOrEmpty(data.SheetName))
            {
                writer.WriteLine($"## Sheet: {data.SheetName}");
            }

            // 直接写入所有行数据
            foreach (var row in data.Rows)
            {
                WriteDataRow(writer, row);
            }

            // 添加空行分隔不同的sheet
            writer.WriteLine();
        }
    }

    /// <summary>
    /// 写入数据行
    /// </summary>
    private void WriteDataRow(StreamWriter writer, List<object> row)
    {
        writer.WriteLine(string.Join(",", row.Select(EscapeCsvField)));
    }

    /// <summary>
    /// 转义CSV字段值
    /// </summary>
    private string EscapeCsvField(object value)
    {
        if (value == null)
        {
            return "";
        }

        string str = value.ToString();
        
        // 如果包含逗号、引号、换行符，需要用引号包裹并转义内部引号
        if (str.Contains(',') || str.Contains('"') || str.Contains('\n') || str.Contains('\r'))
        {
            return $"\"{str.Replace("\"", "\"\"")}\"";
        }

        return str;
    }

    /// <summary>
    /// 清空已记录的数据
    /// </summary>
    public void Clear()
    {
        _csvSourceDatas.Clear();
    }
}

/// <summary>
/// CSV源数据
/// </summary>
public class CsvSourceData
{
    public string SourceFile { get; set; }
    public string SheetName { get; set; }
    public CsvSourceTitle Title { get; set; }
    public List<List<object>> Rows { get; set; } = new();
}

/// <summary>
/// CSV源标题信息
/// </summary>
public class CsvSourceTitle
{
    public List<string> FieldNames { get; set; } = new();
    public List<string> FieldTypes { get; set; } = new();
    public List<string> FieldDescs { get; set; } = new();
}

