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
using Luban.Utils;

namespace Luban.Defs;

public class DefAssembly
{
    private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

    public Dictionary<string, DefTypeBase> Types { get; } = new();

    public List<DefTypeBase> TypeList { get; } = new();

    private readonly Dictionary<string, DefTypeBase> _notCaseSenseTypes = new();

    private readonly Dictionary<string, string> _constAliases = new();

    private readonly HashSet<string> _namespaces = new();

    private readonly Dictionary<string, DefTypeBase> _notCaseSenseNamespaces = new();

    private readonly List<RawTarget> _targets;

    public RawTarget Target { get; }

    public IReadOnlyList<RawTarget> Targets => _targets;

    public RawTarget GetTarget(string targetName)
    {
        return _targets.Find(t => t.Name == targetName);
    }

    private readonly List<DefTable> _exportTables;

    public List<DefTable> ExportTables => _exportTables;

    private Dictionary<string, string> _variants;

    public bool TryGetVariantName(string variantKey, out string variantName)
    {
        if (_variants == null)
        {
            variantName = "";
            return false;
        }
        return _variants.TryGetValue(variantKey, out variantName);
    }

    public bool TryGetVariantNameOrDefault(string variantKey, out string variantName)
    {
        if (_variants == null)
        {
            variantName = "";
            return false;
        }
        if (_variants.TryGetValue(variantKey, out variantName))
        {
            return true;
        }
        return _variants.TryGetValue("default", out variantName);
    }

    public bool TryGetConstAlias(string alias, out string value)
    {
        return _constAliases.TryGetValue(alias, out value);
    }

    public DefAssembly(RawAssembly assembly, string target, List<string> outputTables, List<RawGroup> groupDefs, Dictionary<string, string> variants)
    {
        _targets = assembly.Targets;
        Target = GetTarget(target);
        if (Target == null)
        {
            throw new Exception($"target:{target} is invalid");
        }
        foreach (var g in Target.Groups)
        {
            if (groupDefs.All(d => !d.Names.Contains(g)))
            {
                throw new Exception($"target:{target} group:`{g}` not defined");
            }
        }
        _variants = variants;

        foreach (var c in assembly.ConstAliases)
        {
            _constAliases.Add(c.Key, c.Value);
        }

        foreach (var g in assembly.RefGroups)
        {
            AddRefGroup(g);
        }

        foreach (var e in assembly.Enums)
        {
            AddType(new DefEnum(e));
        }

        foreach (var b in assembly.Beans)
        {
            AddType(new DefBean(b));
        }

        foreach (var p in assembly.Tables)
        {
            var table = new DefTable(p);
            AddType(table);
            AddCfgTable(table);
        }

        _targets.AddRange(assembly.Targets);

        List<DefTable> originTables = GetAllTables();
        if (outputTables.Count == 0)
        {
            _exportTables = originTables.Where(t => NeedExport(t.Groups, groupDefs)).ToList();
        }
        else
        {
            _exportTables = new List<DefTable>();
            foreach (var tableName in outputTables)
            {
                DefTable table = GetCfgTable(tableName);
                if (table != null)
                {
                    _exportTables.Add(table);
                }
                else
                {
                    throw new Exception($"outputTable:{tableName} not found");
                }
            }
        }

        foreach (var table in _exportTables)
        {
            table.IsExported = true;
        }

        foreach (var type in TypeList)
        {
            type.Assembly = this;
        }

        foreach (var type in TypeList)
        {
            type.PreCompile();
        }
        foreach (var type in TypeList)
        {
            type.Compile();
        }

        foreach (var type in TypeList)
        {
            type.PostCompile();
        }
    }

    public bool NeedExport(List<string> groups, List<RawGroup> groupDefs)
    {
        if (groups.Count == 0)
        {
            return groupDefs == null || Target.Groups.Any(g => groupDefs.FirstOrDefault(gd => gd.Names.Contains(g))?.IsDefault == true);
        }
        return groups.Any(g => Target.Groups.Contains(g));
    }


    private readonly Dictionary<string, DefRefGroup> _refGroups = new();

    public Dictionary<string, DefTable> TablesByName { get; } = new();

    public Dictionary<string, DefTable> TablesByFullName { get; } = new();


    private readonly Dictionary<(DefTypeBase, bool), TType> _cacheDefTTypes = new();

    public void AddCfgTable(DefTable table)
    {
        if (!TablesByFullName.TryAdd(table.FullName, table))
        {
            throw new Exception($"table:'{table.FullName}' duplicated");
        }
        if (!TablesByName.TryAdd(table.Name, table))
        {
            throw new Exception($"table:'{table.FullName} 与 table:'{TablesByName[table.Name].FullName}' 的表名重复(不同模块下也不允许定义同名表，将来可能会放开限制)");
        }
    }

    public DefTable GetCfgTable(string name)
    {
        return TablesByFullName.TryGetValue(name, out var t) ? t : null;
    }


    public List<DefTable> GetAllTables()
    {
        return TypeList.Where(t => t is DefTable).Cast<DefTable>().ToList();
    }

    private void AddRefGroup(RawRefGroup g)
    {
        if (_refGroups.ContainsKey(g.Name))
        {
            throw new Exception($"refgroup:{g.Name} 重复");
        }
        _refGroups.Add(g.Name, new DefRefGroup(g));
    }

    public DefRefGroup GetRefGroup(string groupName)
    {
        return _refGroups.TryGetValue(groupName, out var refGroup) ? refGroup : null;
    }

    public void AddType(DefTypeBase type)
    {
        string fullName = type.FullName;
        if (Types.ContainsKey(fullName))
        {
            throw new Exception($"type:'{fullName}' duplicate");
        }

        if (!_notCaseSenseTypes.TryAdd(fullName.ToLower(), type))
        {
            throw new Exception($"type:'{fullName}' 和 type:'{_notCaseSenseTypes[fullName.ToLower()].FullName}' 类名小写重复. 在win平台有问题");
        }

        string namespaze = type.Namespace;
        if (_namespaces.Add(namespaze) && !_notCaseSenseNamespaces.TryAdd(namespaze.ToLower(), type))
        {
            throw new Exception($"type:'{fullName}' 和 type:'{_notCaseSenseNamespaces[namespaze.ToLower()].FullName}' 命名空间小写重复. 在win平台有问题，请修改定义并删除生成的代码目录后再重新生成");
        }

        Types.Add(fullName, type);
        TypeList.Add(type);
    }

    public DefTypeBase GetDefType(string fullName)
    {
        return Types.TryGetValue(fullName, out var type) ? type : null;
    }

    public DefTypeBase GetDefType(string module, string type)
    {
        if (Types.TryGetValue(TypeUtil.MakeFullName(module, type), out var t))
        {
            return t;
        }
        else if (Types.TryGetValue(type, out t))
        {
            return t;
        }
        else
        {
            return null;
        }
    }

    TType GetOrCreateTEnum(DefEnum defType, bool nullable, Dictionary<string, string> tags)
    {
        if (tags == null || tags.Count == 0)
        {
            if (_cacheDefTTypes.TryGetValue((defType, nullable), out var t))
            {
                return t;
            }
            else
            {
                return _cacheDefTTypes[(defType, nullable)] = TEnum.Create(nullable, defType, tags);
            }
        }
        else
        {
            return TEnum.Create(nullable, defType, tags);
            ;
        }
    }

    TType GetOrCreateTBean(DefTypeBase defType, bool nullable, Dictionary<string, string> tags)
    {
        if (tags == null || tags.Count == 0)
        {
            if (_cacheDefTTypes.TryGetValue((defType, nullable), out var t))
            {
                return t;
            }
            else
            {
                return _cacheDefTTypes[(defType, nullable)] = TBean.Create(nullable, (DefBean)defType, tags);
            }
        }
        else
        {
            return TBean.Create(nullable, (DefBean)defType, tags);
        }
    }

    public TType GetDefTType(string module, string type, bool nullable, Dictionary<string, string> tags)
    {
        var defType = GetDefType(module, type);
        switch (defType)
        {
            case DefBean d:
                return GetOrCreateTBean(d, nullable, tags);
            case DefEnum d:
                return GetOrCreateTEnum(d, nullable, tags);
            default:
                return null;
        }
    }

    public TType CreateType(string module, string type, bool containerElementType)
    {
        type = DefUtil.TrimBracePairs(type);
        
        // 检测旧语法并抛出友好错误
        if (type.StartsWith("(array#sep="))
        {
            throw new Exception($"旧的array语法已不再支持,请使用新语法,例如: int[,]");
        }
        if (type.StartsWith("(list#sep="))
        {
            throw new Exception($"旧的list语法已不再支持,请使用新语法,例如: list<int>[,]");
        }
        if (type.StartsWith("(set#sep="))
        {
            throw new Exception($"旧的set语法已不再支持,请使用新语法,例如: set<int>[,]");
        }
        if (type.StartsWith("(map#sep="))
        {
            throw new Exception($"旧的map语法已不再支持,请使用新语法,例如: map<int,string>[,]");
        }
        
        // 检测新语法：包含方括号
        if (type.Contains('['))
        {
            return CreateTypeFromNewBracketSyntax(module, type, containerElementType);
        }
        
        // 检测新语法：包含尖括号（list/set/map）
        if (type.Contains('<'))
        {
            return CreateTypeFromAngleBracketSyntax(module, type, containerElementType);
        }
        
        // 旧语法：容器类型后跟逗号或分号
        int sepIndex = DefUtil.IndexOfBaseTypeEnd(type);
        if (sepIndex > 0)
        {
            string containerTypeAndTags = DefUtil.TrimBracePairs(type.Substring(0, sepIndex));
            var elementTypeAndTags = type.Substring(sepIndex + 1);
            var (containerType, containerTags) = DefUtil.ParseTypeAndVaildAttrs(containerTypeAndTags);
            return CreateContainerType(module, containerType, containerTags, elementTypeAndTags.Trim());
        }
        else
        {
            return CreateNotContainerType(module, type, containerElementType);
        }
    }

    private TType CreateTypeFromNewBracketSyntax(string module, string typeStr, bool containerElementType)
    {
        // 解析方括号语法
        var (baseType, separators, hasAngleBracket) = DefUtil.ParseBracketSyntax(typeStr);
        
        if (separators.Count == 0)
        {
            // 没有方括号，不应该进入这个分支
            return CreateType(module, typeStr, containerElementType);
        }

        // 解析baseType和tags
        var (pureBaseType, tags) = DefUtil.ParseTypeAndVaildAttrs(baseType);
        
        // 如果有尖括号，说明是list/set/map类型
        if (hasAngleBracket)
        {
            var (containerName, angleBracketContent, remaining) = DefUtil.ExtractAngleBracketContent(pureBaseType);
            
            switch (containerName.ToLower())
            {
                case "list":
                    return CreateListFromNewSyntax(module, angleBracketContent, separators, tags);
                case "set":
                    return CreateSetFromNewSyntax(module, angleBracketContent, separators, tags);
                case "map":
                    return CreateMapFromNewSyntax(module, angleBracketContent, separators, tags);
                default:
                    throw new Exception($"不支持的容器类型: {containerName}");
            }
        }
        else
        {
            // 纯数组类型，如 int[,] 或 int[;][,]
            return CreateArrayFromNewSyntax(module, pureBaseType, separators, tags);
        }
    }

    private TType CreateTypeFromAngleBracketSyntax(string module, string typeStr, bool containerElementType)
    {
        // 只有尖括号没有方括号的情况，可能是不完整的语法
        var (containerName, angleBracketContent, remaining) = DefUtil.ExtractAngleBracketContent(typeStr);
        
        // 检查是否缺少方括号
        if (!remaining.Contains('['))
        {
            throw new Exception($"{containerName}类型必须指定分隔符: {containerName}<{angleBracketContent}>[sep]");
        }
        
        // 有方括号，继续用方括号语法处理
        return CreateTypeFromNewBracketSyntax(module, typeStr, containerElementType);
    }

    private TType CreateArrayFromNewSyntax(string module, string elementTypeStr, List<string> separators, Dictionary<string, string> tags)
    {
        // 从最内层开始递归创建数组
        // 例如 int[;][,] 应该创建 array<sep=;>(array<sep=,>(int))
        
        if (separators.Count == 0)
        {
            throw new Exception($"数组必须指定至少一个分隔符");
        }

        // 最内层元素类型
        TType elementType = CreateType(module, elementTypeStr, true);
        
        // 从最后一个分隔符开始，逐层包装
        for (int i = separators.Count - 1; i >= 0; i--)
        {
            var containerTags = new Dictionary<string, string>();
            if (i == 0 && tags != null)
            {
                // 最外层添加用户指定的tags
                containerTags = new Dictionary<string, string>(tags);
            }
            containerTags["sep"] = separators[i];
            
            elementType = TArray.Create(false, containerTags, elementType);
        }
        
        return elementType;
    }

    private TType CreateListFromNewSyntax(string module, string elementTypeStr, List<string> separators, Dictionary<string, string> tags)
    {
        if (separators.Count == 0)
        {
            throw new Exception($"list类型必须指定分隔符: list<{elementTypeStr}>[sep]");
        }
        
        if (separators.Count > 1)
        {
            // list嵌套在array中：list<T>[sep1][sep2]
            // 应该创建：array<sep=sep2>(list<sep=sep1>(T))
            var containerTags = new Dictionary<string, string>(tags ?? new Dictionary<string, string>());
            containerTags["sep"] = separators[0];
            
            TType listType = TList.Create(false, containerTags, CreateType(module, elementTypeStr, true), true);
            
            // 外层用array包装
            return CreateArrayFromNewSyntax(module, "", separators.Skip(1).ToList(), tags) switch
            {
                TArray arr => TArray.Create(false, arr.Tags, listType),
                _ => throw new Exception("内部错误: 期望array类型")
            };
        }
        else
        {
            // 简单的list: list<T>[sep]
            var containerTags = new Dictionary<string, string>(tags ?? new Dictionary<string, string>());
            containerTags["sep"] = separators[0];
            return TList.Create(false, containerTags, CreateType(module, elementTypeStr, true), true);
        }
    }

    private TType CreateSetFromNewSyntax(string module, string elementTypeStr, List<string> separators, Dictionary<string, string> tags)
    {
        if (separators.Count == 0)
        {
            throw new Exception($"set类型必须指定分隔符: set<{elementTypeStr}>[sep]");
        }
        
        if (separators.Count > 1)
        {
            throw new Exception($"set类型只支持一个分隔符,不支持多维: set<{elementTypeStr}>[{separators[0]}]");
        }
        
        var containerTags = new Dictionary<string, string>(tags ?? new Dictionary<string, string>());
        containerTags["sep"] = separators[0];
        
        TType elementType = CreateType(module, elementTypeStr, true);
        if (elementType.IsCollection)
        {
            throw new Exception($"set的元素不支持容器类型");
        }
        
        return TSet.Create(false, containerTags, elementType, false);
    }

    private TType CreateMapFromNewSyntax(string module, string keyValueTypeStr, List<string> separators, Dictionary<string, string> tags)
    {
        if (separators.Count == 0)
        {
            throw new Exception($"map类型必须指定entry分隔符: map<{keyValueTypeStr}>[sep]");
        }
        
        if (separators.Count > 1)
        {
            // map嵌套在array中
            var containerTags = new Dictionary<string, string>(tags ?? new Dictionary<string, string>());
            containerTags["sep"] = separators[0];
            
            var (keyType, valueType) = DefUtil.SplitMapKeyValueType(keyValueTypeStr);
            TType mapType = TMap.Create(false, containerTags, 
                CreateType(module, keyType, true),
                CreateType(module, valueType, true),
                false);
            
            // 外层用array包装
            var remainingSeps = separators.Skip(1).ToList();
            TType outerType = mapType;
            foreach (var sep in remainingSeps)
            {
                var outerTags = new Dictionary<string, string> { ["sep"] = sep };
                outerType = TArray.Create(false, outerTags, outerType);
            }
            return outerType;
        }
        else
        {
            var containerTags = new Dictionary<string, string>(tags ?? new Dictionary<string, string>());
            containerTags["sep"] = separators[0];
            
            var (keyType, valueType) = DefUtil.SplitMapKeyValueType(keyValueTypeStr);
            return TMap.Create(false, containerTags,
                CreateType(module, keyType, true),
                CreateType(module, valueType, true),
                false);
        }
    }

    protected TType CreateNotContainerType(string module, string rawType, bool containerElementType)
    {
        bool defaultAble = true;
        bool nullable = false;
        // 去掉 rawType 两侧的匹配的 ()
        rawType = DefUtil.TrimBracePairs(rawType);
        var (type, tags) = DefUtil.ParseTypeAndVaildAttrs(rawType);

        // 检测是否是list/set/map但缺少尖括号
        string lowerType = type.ToLower();
        if (lowerType == "list" || lowerType == "set" || lowerType == "map")
        {
            throw new Exception($"{lowerType}类型必须指定元素类型: {lowerType}<T>[sep]");
        }

        while (true)
        {
            if (type.EndsWith('?'))
            {
                if (containerElementType)
                {
                    throw new Exception($"container element type can't be nullable type:'{module}.{type}'");
                }
                nullable = true;
                type = type[..^1];
                continue;
            }

            if (type.EndsWith("!"))
            {
                defaultAble = false;
                type = type[..^1];
                continue;
            }
            break;
        }

        if (!defaultAble)
        {
            tags.TryAdd("not-default", "1");
        }

        switch (type)
        {
            case "bool":
                return TBool.Create(nullable, tags);
            case "uint8":
            case "byte":
                return TByte.Create(nullable, tags);
            case "int16":
            case "short":
                return TShort.Create(nullable, tags);
            case "int32":
            case "int":
                return TInt.Create(nullable, tags);
            case "int64":
            case "long":
                return TLong.Create(nullable, tags, false);
            case "bigint":
                return TLong.Create(nullable, tags, true);
            case "float32":
            case "float":
                return TFloat.Create(nullable, tags);
            case "float64":
            case "double":
                return TDouble.Create(nullable, tags);
            case "string":
                return TString.Create(nullable, tags);
            case "text":
                tags.Add("text", "1");
                return TString.Create(nullable, tags);
            case "time":
            case "datetime":
                return TDateTime.Create(nullable, tags);
            default:
            {
                var dtype = GetDefTType(module, type, nullable, tags);
                if (dtype != null)
                {
                    return dtype;
                }
                else
                {
                    throw new ArgumentException($"invalid type. module:'{module}' type:'{type}'");
                }
            }
        }
    }

    TMap CreateMapType(string module, Dictionary<string, string> tags, string keyValueType, bool isTreeMap)
    {
        int typeSepIndex = DefUtil.IndexOfElementTypeSep(keyValueType);
        if (typeSepIndex <= 0 || typeSepIndex >= keyValueType.Length - 1)
        {
            throw new ArgumentException($"invalid map element type:'{keyValueType}'");
        }
        return TMap.Create(false, tags,
            CreateNotContainerType(module, keyValueType.Substring(0, typeSepIndex).Trim(), true),
            CreateType(module, keyValueType.Substring(typeSepIndex + 1).Trim(), true), isTreeMap);
    }

    TType CreateContainerType(string module, string containerType, Dictionary<string, string> containerTags, string elementType)
    {
        switch (containerType)
        {
            case "array":
            {
                return TArray.Create(false, containerTags, CreateType(module, elementType, true));
            }
            case "list":
                return TList.Create(false, containerTags, CreateType(module, elementType, true), true);
            case "set":
            {
                TType type = CreateType(module, elementType, true);
                if (type.IsCollection)
                {
                    throw new Exception("set的元素不支持容器类型");
                }
                return TSet.Create(false, containerTags, type, false);
            }
            case "map":
                return CreateMapType(module, containerTags, elementType, false);
            default:
            {
                throw new ArgumentException($"invalid container type. module:'{module}' container:'{containerType}' element:'{elementType}'");
            }
        }
    }
}
