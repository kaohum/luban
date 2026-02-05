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

using Luban.Datas;

namespace Luban.DataExporter.Builtin.FieldValueReference;

/// <summary>
/// 字段值引用信息
/// </summary>
public class FieldValueReferenceInfo
{
    /// <summary>
    /// 引用该值的表名
    /// </summary>
    public string TableName { get; set; }
    
    /// <summary>
    /// 引用该值的记录ID（主键值）
    /// </summary>
    public string RecordId { get; set; }
    
    /// <summary>
    /// 引用该值的字段名
    /// </summary>
    public string FieldName { get; set; }
    
    /// <summary>
    /// 字段路径（用于嵌套字段）
    /// </summary>
    public string FieldPath { get; set; }
    
    /// <summary>
    /// 字段值（用于显示）
    /// </summary>
    public string FieldValue { get; set; }
    
    /// <summary>
    /// 引用记录的所有字段值（用于模板中查找分组字段）
    /// </summary>
    public Dictionary<string, string> RecordFields { get; set; } = new Dictionary<string, string>();
}

/// <summary>
/// 目标值信息
/// </summary>
public class TargetValueInfo
{
    /// <summary>
    /// 目标记录ID
    /// </summary>
    public string RecordId { get; set; }
    
    /// <summary>
    /// 目标字段值
    /// </summary>
    public DType FieldValue { get; set; }
    
    /// <summary>
    /// 目标字段值的字符串表示
    /// </summary>
    public string FieldValueString { get; set; }
    
    /// <summary>
    /// 引用该值的所有位置
    /// </summary>
    public List<FieldValueReferenceInfo> References { get; set; } = new List<FieldValueReferenceInfo>();
}

/// <summary>
/// 字段值引用收集器
/// </summary>
public class FieldValueReferenceCollection
{
    /// <summary>
    /// 目标表名
    /// </summary>
    public string TargetTable { get; set; }
    
    /// <summary>
    /// 目标字段名
    /// </summary>
    public string TargetField { get; set; }
    
    /// <summary>
    /// 所有目标值及其引用信息
    /// </summary>
    public List<TargetValueInfo> TargetValues { get; set; } = new List<TargetValueInfo>();
    
    /// <summary>
    /// 添加引用信息
    /// </summary>
    public void AddReference(DType targetValue, string tableName, string recordId, string fieldName, string fieldPath, string fieldValue, Dictionary<string, string> recordFields)
    {
        // 查找或创建目标值信息
        var targetValueString = GetValueString(targetValue);
        var targetInfo = TargetValues.FirstOrDefault(t => t.FieldValueString == targetValueString);
        
        if (targetInfo == null)
        {
            targetInfo = new TargetValueInfo
            {
                FieldValue = targetValue,
                FieldValueString = targetValueString
            };
            TargetValues.Add(targetInfo);
        }
        
        // 添加引用信息
        targetInfo.References.Add(new FieldValueReferenceInfo
        {
            TableName = tableName,
            RecordId = recordId,
            FieldName = fieldName,
            FieldPath = fieldPath,
            FieldValue = fieldValue,
            RecordFields = recordFields
        });
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
            DFloat d => d.Value.ToString(),
            DDouble d => d.Value.ToString(),
            DEnum d => d.Value.ToString(),
            DString d => d.Value,
            _ => data.ToString()
        };
    }
    
    /// <summary>
    /// 获取引用该值的表数量
    /// </summary>
    public int GetTotalTableCount()
    {
        return TargetValues
            .SelectMany(t => t.References)
            .Select(r => r.TableName)
            .Distinct()
            .Count();
    }
    
    /// <summary>
    /// 获取引用该值的记录数量
    /// </summary>
    public int GetTotalRecordCount()
    {
        return TargetValues
            .SelectMany(t => t.References)
            .Count();
    }
}

