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

using Luban.CodeFormat;
using Luban.CodeTarget;
using Luban.CSharp.TemplateExtensions;
using Luban.Datas;
using Luban.Defs;
using Scriban;
using Scriban.Runtime;

namespace Luban.CSharp.CodeTarget;

/// <summary>
/// 字段值引用信息（内部类）
/// </summary>
internal class FieldValueReferenceInfo
{
    public string TableName { get; set; }
    public string RecordId { get; set; }
    public string FieldName { get; set; }
    public string FieldPath { get; set; }
    public string FieldValue { get; set; }
}

/// <summary>
/// 目标值信息（内部类）
/// </summary>
internal class TargetValueInfo
{
    public string RecordId { get; set; }
    public DType FieldValue { get; set; }
    public string FieldValueString { get; set; }
    public List<FieldValueReferenceInfo> References { get; set; } = new List<FieldValueReferenceInfo>();
}

/// <summary>
/// 字段值引用代码生成目标
/// 用于生成字段值引用查询的代码
/// 使用方式：--codeTarget cs-field-value-reference -x targetTable=表名 -x targetField=字段名
/// </summary>
[CodeTarget("cs-field-value-reference")]
public class CsharpFieldValueReferenceCodeTarget : CsharpCodeTargetBase
{
    protected override string FileSuffixName => "cs";

    public override void Handle(GenerationContext ctx, OutputFileManifest manifest)
    {
        // 从环境变量中获取目标表和字段
        string targetTable = EnvManager.Current.GetOptionOrDefault("cs-field-value-reference", "targetTable", false, "");
        string targetField = EnvManager.Current.GetOptionOrDefault("cs-field-value-reference", "targetField", false, "");

        if (string.IsNullOrWhiteSpace(targetTable))
        {
            throw new Exception("必须指定 targetTable 参数，例如：-x cs-field-value-reference.targetTable=TbItem");
        }

        if (string.IsNullOrWhiteSpace(targetField))
        {
            throw new Exception("必须指定 targetField 参数，例如：-x cs-field-value-reference.targetField=Id");
        }

        // 查找目标表
        var targetTableDef = ctx.ExportTables.FirstOrDefault(t => 
            t.FullName == targetTable || 
            t.Name == targetTable ||
            t.FullName.EndsWith("." + targetTable) ||
            t.FullName.EndsWith(targetTable));
            
        if (targetTableDef == null)
        {
            var availableTables = string.Join(", ", ctx.ExportTables.Select(t => t.FullName));
            throw new Exception($"未找到目标表：{targetTable}\n可用的表: {availableTables}");
        }

        // 获取目标表的所有记录
        var targetRecords = ctx.GetTableAllDataList(targetTableDef);
        if (targetRecords == null || targetRecords.Count == 0)
        {
            throw new Exception($"目标表 {targetTable} 没有数据");
        }

        // 为每个目标值建立索引
        var targetValueMap = new Dictionary<string, TargetValueInfo>();
        var targetValues = new List<TargetValueInfo>();
        
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
                    targetValues.Add(targetInfo);
                }
            }
        }

        // 遍历所有表，查找引用
        foreach (var table in ctx.ExportTables)
        {
            var records = ctx.GetTableAllDataList(table);
            if (records == null || records.Count == 0)
            {
                continue;
            }

            foreach (var record in records)
            {
                var recordId = GetRecordId(table, record);
                CheckRecordForReferences(record.Data, table.FullName, recordId, "", targetValueMap);
            }
        }

        // 生成代码
        var template = GetTemplate("field-value-reference");
        var tplCtx = CreateTemplateContext(template);
        var extraEnvs = new ScriptObject
        {
            { "__ctx", ctx },
            { "__target_table", targetTable },
            { "__target_field", targetField },
            { "__target_values", targetValues },
            { "__namespace", ctx.Target.TopModule },
            { "__code_style", CodeStyle },
        };
        tplCtx.PushGlobal(extraEnvs);

        var content = template.Render(tplCtx);

        string outputFile = $"FieldValueReference_{targetTable}_{targetField}.cs";
        manifest.AddFile(new OutputFile
        {
            File = outputFile,
            Content = content,
            Encoding = FileEncoding
        });
    }

    protected override void OnCreateTemplateContext(TemplateContext ctx)
    {
        base.OnCreateTemplateContext(ctx);
        ctx.PushGlobal(new CsharpTemplateExtension());
    }

    /// <summary>
    /// 递归检查记录中的所有字段值
    /// </summary>
    private void CheckRecordForReferences(DType data, string tableName, string recordId, string fieldPath, Dictionary<string, TargetValueInfo> targetValueMap)
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
                FieldValue = valueString
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
                        CheckRecordForReferences(fieldValue, tableName, recordId, newPath, targetValueMap);
                    }
                }
                break;

            case DArray array:
                for (int i = 0; i < array.Datas.Count; i++)
                {
                    if (array.Datas[i] != null)
                    {
                        var newPath = $"{fieldPath}[{i}]";
                        CheckRecordForReferences(array.Datas[i], tableName, recordId, newPath, targetValueMap);
                    }
                }
                break;

            case DList list:
                for (int i = 0; i < list.Datas.Count; i++)
                {
                    if (list.Datas[i] != null)
                    {
                        var newPath = $"{fieldPath}[{i}]";
                        CheckRecordForReferences(list.Datas[i], tableName, recordId, newPath, targetValueMap);
                    }
                }
                break;

            case DSet set:
                int setIndex = 0;
                foreach (var item in set.Datas)
                {
                    var newPath = $"{fieldPath}[{setIndex++}]";
                    CheckRecordForReferences(item, tableName, recordId, newPath, targetValueMap);
                }
                break;

            case DMap map:
                int mapIndex = 0;
                foreach (var kvp in map.DataMap)
                {
                    var keyPath = $"{fieldPath}[{mapIndex}].Key";
                    var valuePath = $"{fieldPath}[{mapIndex}].Value";
                    CheckRecordForReferences(kvp.Key, tableName, recordId, keyPath, targetValueMap);
                    CheckRecordForReferences(kvp.Value, tableName, recordId, valuePath, targetValueMap);
                    mapIndex++;
                }
                break;
        }
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

