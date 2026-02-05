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
using Luban.Datas;
using Luban.Defs;
using Luban.Types;
using System.Text;

namespace Luban.DataExporter.Builtin.FieldValueReference;

/// <summary>
/// 字段值引用数据导出目标
/// 用于导出指定表字段的所有值的引用信息
/// 使用方式：--dataTarget field-value-reference -x targetTable=表名 -x targetField=字段名
/// </summary>
[DataTarget("field-value-reference")]
public class FieldValueReferenceDataTarget : DataTargetBase
{
    protected override string DefaultOutputFileExt => "txt";

    public override bool ExportAllRecords => true;

    public override AggregationType AggregationType => AggregationType.Tables;

    public override OutputFile ExportTable(DefTable table, List<Record> records)
    {
        throw new NotImplementedException();
    }

    public override OutputFile ExportTables(List<DefTable> tables)
    {
        // 从环境变量中获取目标表和字段
        string targetTable = EnvManager.Current.GetOptionOrDefault("field-value-reference", "targetTable", false, "");
        string targetField = EnvManager.Current.GetOptionOrDefault("field-value-reference", "targetField", false, "");

        if (string.IsNullOrWhiteSpace(targetTable))
        {
            throw new Exception("必须指定 targetTable 参数，例如：-x field-value-reference.targetTable=TbItem");
        }

        if (string.IsNullOrWhiteSpace(targetField))
        {
            throw new Exception("必须指定 targetField 参数，例如：-x field-value-reference.targetField=Id");
        }

        // 查找目标表
        var targetTableDef = tables.FirstOrDefault(t => 
            t.FullName == targetTable || 
            t.Name == targetTable ||
            t.FullName.EndsWith("." + targetTable) ||
            t.FullName.EndsWith(targetTable));
            
        if (targetTableDef == null)
        {
            var availableTables = string.Join(", ", tables.Select(t => t.FullName));
            throw new Exception($"未找到目标表：{targetTable}\n可用的表: {availableTables}");
        }

        // 获取目标表的所有记录
        var targetRecords = GenerationContext.Current.GetTableAllDataList(targetTableDef);
        if (targetRecords == null || targetRecords.Count == 0)
        {
            throw new Exception($"目标表 {targetTable} 没有数据");
        }

        // 创建收集器
        var collection = new FieldValueReferenceCollection
        {
            TargetTable = targetTable,
            TargetField = targetField
        };

        // 为每个目标值建立索引
        var targetValueMap = new Dictionary<string, TargetValueInfo>();
        foreach (var record in targetRecords)
        {
            var fieldValue = record.Data.GetField(targetField);
            if (fieldValue != null)
            {
                var valueString = GetValueString(fieldValue);
                var recordId = GetRecordId(targetTableDef, record);
                
                if (!targetValueMap.ContainsKey(valueString))
                {
                    var targetInfo = new TargetValueInfo
                    {
                        RecordId = recordId,
                        FieldValue = fieldValue,
                        FieldValueString = valueString
                    };
                    targetValueMap[valueString] = targetInfo;
                    collection.TargetValues.Add(targetInfo);
                }
            }
        }

        // 遍历所有表，查找引用
        foreach (var table in tables)
        {
            var records = GenerationContext.Current.GetTableAllDataList(table);
            if (records == null || records.Count == 0)
            {
                continue;
            }

            foreach (var record in records)
            {
                var recordId = GetRecordId(table, record);
                var recordFields = GetAllFieldsFromRecord(table, record);
                CheckRecordForReferences(record.Data, table.FullName, recordId, "", targetValueMap, recordFields);
            }
        }

        // 生成输出内容
        var sb = new StringBuilder();
        sb.AppendLine($"# 字段值引用分析报告");
        sb.AppendLine($"# 目标表: {targetTable}");
        sb.AppendLine($"# 目标字段: {targetField}");
        sb.AppendLine($"# 生成时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();
        sb.AppendLine($"# 总体统计:");
        sb.AppendLine($"# - 目标值数量: {collection.TargetValues.Count}");
        sb.AppendLine($"# - 引用该字段的表数量: {collection.GetTotalTableCount()}");
        sb.AppendLine($"# - 引用该字段的记录数量: {collection.GetTotalRecordCount()}");
        sb.AppendLine();

        // 为每个目标值生成报告
        foreach (var targetInfo in collection.TargetValues.OrderBy(t => t.FieldValueString))
        {
            sb.AppendLine("# " + new string('=', 80));
            sb.AppendLine($"# 目标值: {targetInfo.FieldValueString} (记录ID: {targetInfo.RecordId})");
            sb.AppendLine("# " + new string('=', 80));
            sb.AppendLine($"# 引用该值的记录数量: {targetInfo.References.Count}");
            sb.AppendLine();

            if (targetInfo.References.Count == 0)
            {
                sb.AppendLine("# 未找到引用");
            }
            else
            {
                sb.AppendLine("# 格式: 表名 | 记录ID | 字段名 | 字段路径 | 字段值");
                sb.AppendLine("# " + new string('-', 80));
                
                foreach (var reference in targetInfo.References.OrderBy(r => r.TableName).ThenBy(r => r.RecordId))
                {
                    sb.AppendLine($"{reference.TableName} | {reference.RecordId} | {reference.FieldName} | {reference.FieldPath} | {reference.FieldValue}");
                }
            }
            sb.AppendLine();
        }

        // 输出文件名
        string outputFile = EnvManager.Current.GetOptionOrDefault(
            "field-value-reference", 
            "outputFile", 
            false, 
            $"field_value_reference_{targetTable}_{targetField}.txt");

        return CreateOutputFile(outputFile, sb.ToString());
    }

    /// <summary>
    /// 递归检查记录中的所有字段值
    /// </summary>
    private void CheckRecordForReferences(DType data, string tableName, string recordId, string fieldPath, Dictionary<string, TargetValueInfo> targetValueMap, Dictionary<string, string> recordFields)
    {
        if (data == null)
        {
            return;
        }

        // 检查当前值是否匹配目标值
        var valueString = GetValueString(data);
        if (targetValueMap.TryGetValue(valueString, out var targetInfo))
        {
            var fieldName = string.IsNullOrEmpty(fieldPath) ? "?" : fieldPath.Split('.').Last();
            targetInfo.References.Add(new FieldValueReferenceInfo
            {
                TableName = tableName,
                RecordId = recordId,
                FieldName = fieldName,
                FieldPath = fieldPath,
                FieldValue = valueString,
                RecordFields = recordFields
            });
        }

        // 递归检查嵌套结构
        switch (data)
        {
            case DBean bean:
                var defFields = bean.ImplType.HierarchyFields;
                for (int i = 0; i < bean.Fields.Count && i < defFields.Count; i++)
                {
                    var fieldValue = bean.Fields[i];
                    if (fieldValue != null)
                    {
                        var defField = defFields[i];
                        var newPath = string.IsNullOrEmpty(fieldPath) ? defField.Name : $"{fieldPath}.{defField.Name}";
                        CheckRecordForReferences(fieldValue, tableName, recordId, newPath, targetValueMap, recordFields);
                    }
                }
                break;

            case DArray array:
                for (int i = 0; i < array.Datas.Count; i++)
                {
                    if (array.Datas[i] != null)
                    {
                        var newPath = $"{fieldPath}[{i}]";
                        CheckRecordForReferences(array.Datas[i], tableName, recordId, newPath, targetValueMap, recordFields);
                    }
                }
                break;

            case DList list:
                for (int i = 0; i < list.Datas.Count; i++)
                {
                    if (list.Datas[i] != null)
                    {
                        var newPath = $"{fieldPath}[{i}]";
                        CheckRecordForReferences(list.Datas[i], tableName, recordId, newPath, targetValueMap, recordFields);
                    }
                }
                break;

            case DSet set:
                int setIndex = 0;
                foreach (var item in set.Datas)
                {
                    var newPath = $"{fieldPath}[{setIndex++}]";
                    CheckRecordForReferences(item, tableName, recordId, newPath, targetValueMap, recordFields);
                }
                break;

            case DMap map:
                int mapIndex = 0;
                foreach (var kvp in map.DataMap)
                {
                    var keyPath = $"{fieldPath}[{mapIndex}].Key";
                    var valuePath = $"{fieldPath}[{mapIndex}].Value";
                    CheckRecordForReferences(kvp.Key, tableName, recordId, keyPath, targetValueMap, recordFields);
                    CheckRecordForReferences(kvp.Value, tableName, recordId, valuePath, targetValueMap, recordFields);
                    mapIndex++;
                }
                break;
        }
    }
    
    /// <summary>
    /// 获取记录的所有字段值
    /// </summary>
    private Dictionary<string, string> GetAllFieldsFromRecord(DefTable table, Record record)
    {
        var fields = new Dictionary<string, string>();
        var bean = record.Data;
        var defFields = bean.ImplType.HierarchyFields;
        
        for (int i = 0; i < bean.Fields.Count && i < defFields.Count; i++)
        {
            var fieldValue = bean.Fields[i];
            var defField = defFields[i];
            
            if (fieldValue != null)
            {
                fields[defField.Name] = GetValueString(fieldValue);
            }
        }
        
        return fields;
    }

    /// <summary>
    /// 获取记录的ID
    /// </summary>
    private string GetRecordId(DefTable table, Record record)
    {
        if (table.IndexField != null)
        {
            var idValue = record.Data.GetField(table.IndexField.Name);
            return GetValueString(idValue);
        }
        return "?";
    }

    /// <summary>
    /// 获取值的字符串表示
    /// </summary>
    private string GetValueString(DType data)
    {
        if (data == null) return "null";
        
        return data switch
        {
            DBool d => d.Value.ToString(),
            DByte d => d.Value.ToString(),
            DShort d => d.Value.ToString(),
            DInt d => d.Value.ToString(),
            DLong d => d.Value.ToString(),
            DFloat d => d.Value.ToString("F2"),
            DDouble d => d.Value.ToString("F2"),
            DEnum d => d.Value.ToString(),
            DString d => d.Value,
            _ => data.ToString()
        };
    }
}

