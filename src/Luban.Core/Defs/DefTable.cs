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

using Luban.RawDefs;
using Luban.Types;
using Luban.TypeVisitors;
using Luban.Utils;
using Luban.Validator;

namespace Luban.Defs;

public record class IndexInfo(List<DefField> IndexFields, List<int> IndexFieldIdIndexes)
{
    // 是否为组合索引
    public bool IsUnionIndex => IndexFields.Count > 1;
    
    // 是否为多值索引（一个key对应多个value）
    public bool IsMultiValue { get; set; } = false;
    
    // 主索引字段（第一个字段）
    public DefField PrimaryIndexField => IndexFields[0];
    
    // 主索引字段的ID索引
    public int PrimaryIndexFieldIdIndex => IndexFieldIdIndexes[0];
    
    // 获取索引名称（用于显示）
    public string IndexName => string.Join("+", IndexFields.Select(f => f.Name));
    
    // 兼容旧代码的属性
    public TType Type => IndexFields[0].CType;
    public DefField IndexField => IndexFields[0];
    public int IndexFieldIdIndex => IndexFieldIdIndexes[0];
};

public class DefTable : DefTypeBase
{
    private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

    public DefTable(RawTable b)
    {
        Name = b.Name;
        Namespace = b.Namespace;
        Index = b.Index;
        ValueType = b.ValueType;
        Mode = b.Mode;
        InputFiles = b.InputFiles;
        Groups = b.Groups;
        Comment = b.Comment;
        ReadSchemaFromFile = b.ReadSchemaFromFile;
        Tags = b.Tags;
        _outputFile = b.OutputFile;
    }

    public string Index { get; private set; }

    public string ValueType { get; }

    public TableMode Mode { get; }

    public bool ReadSchemaFromFile { get; }

    public bool IsSingletonTable => Mode == TableMode.ONE;

    public bool IsMapTable => Mode == TableMode.MAP;

    public bool IsListTable => Mode == TableMode.LIST;

    public bool IsExported { get; set; }

    public List<string> InputFiles { get; }

    private readonly string _outputFile;

    public TType KeyTType { get; private set; }

    public DefField IndexField { get; private set; }

    public int IndexFieldIdIndex { get; private set; }

    public TBean ValueTType { get; private set; }

    public TType Type { get; private set; }

    public bool IsUnionIndex { get; private set; }

    public bool MultiKey { get; private set; }

    public List<IndexInfo> IndexList { get; } = new();

    public List<ITableValidator> Validators { get; } = new();

    public string OutputDataFile => string.IsNullOrWhiteSpace(_outputFile) ? FullName.Replace('.', '_').ToLower() : _outputFile;

    public override void Compile()
    {
        var ass = Assembly;

        if ((ValueTType = (TBean)ass.CreateType(Namespace, ValueType, false)) == null)
        {
            throw new Exception($"table:'{FullName}' 的 value类型:'{ValueType}' 不存在");
        }

        switch (Mode)
        {
            case TableMode.ONE:
            {
                IsUnionIndex = false;
                KeyTType = null;
                Type = ValueTType;
                break;
            }
            case TableMode.MAP:
            {
                IsUnionIndex = true;
                if (!string.IsNullOrWhiteSpace(Index))
                {
                    if (ValueTType.DefBean.TryGetField(Index, out var f, out var i))
                    {
                        IndexField = f;
                        IndexFieldIdIndex = i;
                    }
                    else
                    {
                        throw new Exception($"table:'{FullName}' index:'{Index}' 字段不存在");
                    }
                }
                else if (ValueTType.DefBean.HierarchyFields.Count == 0)
                {
                    throw new Exception($"table:'{FullName}' 必须定义至少一个字段");
                }
                else
                {
                    IndexField = ValueTType.DefBean.HierarchyFields[0];
                    Index = IndexField.Name;
                    IndexFieldIdIndex = 0;
                }
                KeyTType = IndexField.CType;
                Type = TMap.Create(false, null, KeyTType, ValueTType, false);
                this.IndexList.Add(new IndexInfo(new List<DefField> { IndexField }, new List<int> { IndexFieldIdIndex }));
                break;
            }
            case TableMode.LIST:
            {
                // 先按逗号分割，得到各个索引项（可能是单个字段或组合字段）
                var indexItems = Index.Split(',').Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
                
                foreach (var indexItem in indexItems)
                {
                    // 对每个索引项按加号分割，判断是单个索引还是组合索引
                    var fieldNames = indexItem.Split('+').Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToList();
                    
                    var indexFields = new List<DefField>();
                    var indexFieldIdIndexes = new List<int>();
                    
                    foreach (var fieldName in fieldNames)
                    {
                        if (ValueTType.DefBean.TryGetField(fieldName, out var f, out var i))
                        {
                            indexFields.Add(f);
                            indexFieldIdIndexes.Add(i);
                            
                            // 设置主索引字段（第一个字段）
                            if (IndexField == null)
                            {
                                IndexField = f;
                                IndexFieldIdIndex = i;
                            }
                        }
                        else
                        {
                            throw new Exception($"table:'{FullName}' index:'{fieldName}' 字段不存在");
                        }
                    }
                    
                    this.IndexList.Add(new IndexInfo(indexFields, indexFieldIdIndexes));
                }
                
                // 判断是否有组合索引或多个独立索引
                // MultiKey: 多个独立索引（用逗号分隔）
                // IsUnionIndex: 有至少一个组合索引（用加号连接）
                MultiKey = IndexList.Count > 1 && IndexList.All(idx => !idx.IsUnionIndex);
                IsUnionIndex = IndexList.Any(idx => idx.IsUnionIndex);
                break;
            }
            default:
                throw new Exception($"unknown mode:'{Mode}'");
        }

        foreach (var index in IndexList)
        {
            TType indexType = index.Type;
            string idxName = index.IndexField.Name;
            if (indexType.IsNullable)
            {
                throw new Exception($"table:'{FullName}' index:'{idxName}' 不能为 nullable类型");
            }
            if (!indexType.Apply(IsValidTableKeyTypeVisitor.Ins))
            {
                throw new Exception($"table:'{FullName}' index:'{idxName}' 的类型:'{index.IndexField.Type}' 不能作为index");
            }
        }
    }
}
