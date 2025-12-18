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

﻿using System;
﻿using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Linq;
using System.Text;
using Luban.CodeFormat;
using Luban.CodeTarget;
using Luban.DataLoader;
using Luban.Datas;
using Luban.Defs;
using Luban.L10N;
using Luban.RawDefs;
using Luban.Schema;
using Luban.Types;
using Luban.TypeVisitors;
using Luban.Utils;
using Luban.Validator;

namespace Luban;

public class GenerationContextBuilder
{
    public DefAssembly Assembly { get; set; }

    public List<string> IncludeTags { get; set; }

    public List<string> ExcludeTags { get; set; }

    public string TimeZone { get; set; }
}

public class GenerationContext
{
    private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

    public static GenerationContext Current { get; private set; }

    public static ICodeTarget CurrentCodeTarget { get; set; }

    public static LubanConfig GlobalConf { get; set; }

    public DefAssembly Assembly { get; private set; }

    public RawTarget Target => Assembly.Target;

    public List<string> IncludeTags { get; private set; }

    public List<string> ExcludeTags { get; private set; }

    // 供模板等场景使用的“当前所有有效环境 tag”，通常为命令行 -i 传入的 tag
    // 再加上默认基础环境 tag(_base_)
    public List<string> AllTags { get; private set; }

    private readonly ConcurrentDictionary<string, TableDataInfo> _recordsByTables = new();

    private readonly Dictionary<string, List<DefTable>> _tablesByTag = new();

    private readonly object _tablesByTagLock = new();

    private bool _datasLoaded;

    private readonly object _loadDatasLock = new();

    private List<L10NKeyInfo> _l10nKeyInfos;

    // dataExporter 名称（例如 default、l10n-bin-split），供模板感知当前导出模式
    public string DataExporterName { get; private set; }

    // l10n 相关配置，便于模板直接复用
    public IReadOnlyList<string> L10NLanguages { get; private set; } = Array.Empty<string>();

    public string L10NTextKeyFieldName { get; private set; }
    public string L10NTextKeyFieldDesc { get; private set; }

    public bool IsL10NBinarySplitDataExporter { get; private set; }

    public string TopModule => Target.TopModule;

    public List<DefTable> Tables => Assembly.GetAllTables();

    private List<DefTypeBase> ExportTypes { get; set; }

    public List<DefTable> ExportTables { get; private set; }

    public List<DefBean> ExportBeans { get; private set; }

    public List<DefEnum> ExportEnums { get; private set; }

    public TimeZoneInfo TimeZone { get; private set; }

    public ITextProvider TextProvider { get; private set; }

    private readonly Dictionary<string, object> _uniqueObjects = new();

    private readonly HashSet<Type> _failedValidatorTypes = new();

    private bool _exportEmptyGroupsTypes;

    public bool DatasLoaded => _datasLoaded;

    // key: tag (已统一为小写)，value: 在该 tag 下实际有数据导出的表列表
    public IReadOnlyDictionary<string, List<DefTable>> TablesByTag
    {
        get
        {
            lock (_tablesByTagLock)
            {
                return new Dictionary<string, List<DefTable>>(_tablesByTag);
            }
        }
    }

    public void LoadDatas()
    {
        if (_datasLoaded)
        {
            s_logger.Info("load datas skip (already loaded)");
            return;
        }

        lock (_loadDatasLock)
        {
            if (_datasLoaded)
            {
                s_logger.Info("load datas skip (already loaded)");
                return;
            }

            s_logger.Info("load datas begin");
            _l10nKeyInfos = null;
            TextProvider?.Load();
            DataLoaderManager.Ins.LoadDatas(this);
            _datasLoaded = true;
            s_logger.Info("load datas end");
        }
    }

    public GenerationContext()
    {
        Current = this;
    }

    public void Init(GenerationContextBuilder builder)
    {
        Assembly = builder.Assembly;
        IncludeTags = builder.IncludeTags;
        ExcludeTags = builder.ExcludeTags;
        if (IncludeTags != null && IncludeTags.Count != 0 && ExcludeTags != null && ExcludeTags.Count > 0)
        {
            throw new Exception("option '--includeTag <tag>' and '--excludeTag <tag>' can not be set at the same time");
        }

        if (IncludeTags != null && IncludeTags.Count > 0)
        {
            var allTags = new List<string>(IncludeTags);
            if (!allTags.Contains(Record.DefaultTag))
            {
                allTags.Add(Record.DefaultTag);
            }
            AllTags = allTags;
        }
        else
        {
            AllTags = new List<string>();
        }

        TimeZone = TimeZoneUtil.GetTimeZone(builder.TimeZone);
        _exportEmptyGroupsTypes = builder.Assembly.Target.Groups.Any(g => GlobalConf.Groups.FirstOrDefault(gd => gd.Names.Contains(g))?.IsDefault == true);

        TextProvider = EnvManager.Current.TryGetOption(BuiltinOptionNames.L10NFamily, BuiltinOptionNames.L10NProviderName, false, out string providerName) ?
            L10NManager.Ins.CreateTextProvider(providerName) : null;

        // 记录 dataExporter 及 l10n 选项，方便模板在生成代码时直接使用
        DataExporterName = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.DataExporter, true, "default");
        L10NLanguages = L10NOptionUtil.GetLanguages();
        L10NTextKeyFieldName = L10NOptionUtil.GetKeyFieldName();
        L10NTextKeyFieldDesc = L10NOptionUtil.GetKeyFieldDesc();
        IsL10NBinarySplitDataExporter = string.Equals(DataExporterName, "l10n-bin-split", StringComparison.OrdinalIgnoreCase);

        // 确保导出用的表在全局范围内按表名稳定排序，便于模板等场景使用（例如 __tables）
        ExportTables = Assembly.ExportTables
            .OrderBy(t => t.Name)
            .ToList();
        ExportTypes = CalculateExportTypes();
        ExportBeans = SortBeanTypes(ExportTypes.OfType<DefBean>().ToList());
        ExportEnums = ExportTypes.OfType<DefEnum>().ToList();
    }

    public IReadOnlyList<L10NKeyInfo> GetL10NKeyInfos()
    {
        if (_l10nKeyInfos != null)
        {
            return _l10nKeyInfos;
        }

        if (!DatasLoaded || L10NLanguages.Count == 0)
        {
            return Array.Empty<L10NKeyInfo>();
        }

        var keyFieldName = L10NTextKeyFieldName;
        var keyFieldDesc = L10NTextKeyFieldDesc;
        var langSet = new HashSet<string>(L10NLanguages, StringComparer.Ordinal);
        var keys = new List<(string, string)>();
        var keySet = new HashSet<string>(StringComparer.Ordinal);

        foreach (var table in ExportTables)
        {
            if (table.ValueTType is not TBean tbean)
            {
                continue;
            }

            var bean = tbean.DefBean;
            if (!HasStringField(bean, keyFieldName) || !HasAnyLanguageField(bean, langSet))
            {
                continue;
            }
            
            if (!_recordsByTables.TryGetValue(table.FullName, out var tableDataInfo) || tableDataInfo.FinalRecords == null)
            {
                continue;
            }
            
            var hasDesc = HasStringField(bean, keyFieldDesc);

            foreach (var record in tableDataInfo.FinalRecords)
            {
                if (record.Data is not DBean data)
                {
                    continue;
                }
                var keyValue = data.GetField(keyFieldName) as DString;
                if (keyValue == null || string.IsNullOrEmpty(keyValue.Value))
                {
                    continue;
                }

                if (keySet.Add(keyValue.Value))
                {
                    var descContent = string.Empty;
                    if (hasDesc)
                    {
                        var descValue = data.GetField(keyFieldDesc) as DString;
                        if (descValue != null && !string.IsNullOrEmpty(descValue.Value))
                        {
                            descContent = descValue.Value;
                        }
                    }
                    s_logger.Info("keys add {} {}, {}", keyValue.Value, descContent, hasDesc);            
                    keys.Add((keyValue.Value, descContent));
                }
            }
        }

        //keys.Sort((v1, v2) => String.Compare(v1.Item1, v2.Item1, StringComparison.Ordinal));
        _l10nKeyInfos = BuildL10NKeyInfos(keys);
        return _l10nKeyInfos;
    }

    private void AddChildrenByOrder(List<DefBean> list, DefBean bean)
    {
        list.Add(bean);
        if (bean.Children == null || bean.Children.Count == 0)
        {
            return;
        }
        var children = new List<DefBean>(bean.Children);
        children.Sort((a, b) => a.FullName.CompareTo(b.FullName));
        foreach (var child in children)
        {
            AddChildrenByOrder(list, child);
        }
    }

    /// <summary>
    /// some languages like c++ have dependencies on the order of type definitions, so we need to sort the types here
    /// </summary>
    /// <param name="types"></param>
    /// <returns></returns>
    private List<DefBean> SortBeanTypes(List<DefBean> types)
    {
        var sortedBeans = new List<DefBean>();
        foreach (var bean in types)
        {
            if (bean.ParentDefType == null)
            {
                AddChildrenByOrder(sortedBeans, bean);
            }
        }
        Debug.Assert(types.Count == sortedBeans.Count);
        return sortedBeans;
    }

    private static bool HasStringField(DefBean bean, string fieldName)
    {
        return bean.Fields.FirstOrDefault(f => string.Equals(f.Name, fieldName, StringComparison.Ordinal))?.CType is TString;
    }

    private static bool HasAnyLanguageField(DefBean bean, HashSet<string> langSet)
    {
        foreach (var f in bean.Fields)
        {
            if (langSet.Contains(f.Name) && f.CType is TString)
            {
                return true;
            }
        }
        return false;
    }

    private static List<L10NKeyInfo> BuildL10NKeyInfos(IEnumerable<(string, string)> keys)
    {
        var result = new List<L10NKeyInfo>();
        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            string fieldName = MakeIdentifier(key.Item1);
            if (nameCount.TryGetValue(fieldName, out int count))
            {
                count++;
                nameCount[fieldName] = count;
                fieldName = $"{fieldName}_{count}";
            }
            else
            {
                nameCount[fieldName] = 1;
            }
            result.Add(new L10NKeyInfo(key.Item1, fieldName, key.Item2));
        }
        return result;
    }

    private static string MakeIdentifier(string key)
    {
        var sb = new StringBuilder();
        foreach (char ch in key)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch == '_' ? ch : '_');
        }

        if (sb.Length == 0 || char.IsDigit(sb[0]))
        {
            sb.Insert(0, '_');
        }

        return sb.ToString();
    }

    private bool NeedExportNotDefault(List<string> groups)
    {
        if (groups.Count == 0)
        {
            return _exportEmptyGroupsTypes;
        }
        return groups.Any(Target.Groups.Contains);
    }

    private List<DefTypeBase> CalculateExportTypes()
    {
        var refTypes = new Dictionary<string, DefTypeBase>();
        var types = Assembly.TypeList;
        foreach (var t in types)
        {
            if (!refTypes.ContainsKey(t.FullName))
            {
                if (t is DefBean bean && NeedExportNotDefault(t.Groups))
                {
                    TBean.Create(false, bean, null).Apply(RefTypeVisitor.Ins, refTypes);
                }
                else if (t is DefEnum && NeedExportNotDefault(t.Groups))
                {
                    refTypes.Add(t.FullName, t);
                }
            }
        }

        foreach (var table in ExportTables)
        {
            refTypes[table.FullName] = table;
            table.ValueTType.Apply(RefTypeVisitor.Ins, refTypes);
        }

        return refTypes.OrderBy(p => p.Key).Select(p => p.Value).ToList();
    }

    public static string GetInputDataPath()
    {
        return GlobalConf.InputDataDir;
    }

    public void AddDataTable(DefTable table, List<Record> mainRecords, List<Record> patchRecords)
    {
        s_logger.Debug("AddDataTable name:{} record count:{}", table.FullName, mainRecords.Count);
        var filteredMain = mainRecords.Where(r => r.IsNotFiltered(IncludeTags, ExcludeTags)).ToList();
        var filteredPatch = patchRecords != null ? patchRecords.Where(r => r.IsNotFiltered(IncludeTags, ExcludeTags)).ToList() : null;
        var tableDataInfo = new TableDataInfo(table, filteredMain, filteredPatch);
        _recordsByTables[table.FullName] = tableDataInfo;

        // 统计各 tag 实际有数据的表列表，供模板使用
        if (AllTags != null && AllTags.Count > 0 && tableDataInfo.FinalRecords != null && tableDataInfo.FinalRecords.Count > 0)
        {
            // 预先构建一个 HashSet，加速包含判断
            var allTagSet = new HashSet<string>(AllTags, StringComparer.OrdinalIgnoreCase);

            lock (_tablesByTagLock)
            {
                foreach (var rec in tableDataInfo.FinalRecords)
                {
                    if (rec.Tags == null || rec.Tags.Count == 0)
                    {
                        continue;
                    }

                    foreach (var rawTag in rec.Tags)
                    {
                        if (string.IsNullOrWhiteSpace(rawTag))
                        {
                            continue;
                        }
                        var tag = rawTag.Trim().ToLowerInvariant();
                        if (!allTagSet.Contains(tag))
                        {
                            continue;
                        }

                        if (!_tablesByTag.TryGetValue(tag, out var list))
                        {
                            list = new List<DefTable>();
                            _tablesByTag.Add(tag, list);
                        }
                        if (!list.Contains(table))
                        {
                            list.Add(table);
                            list.Sort((a, b) => string.Compare(a.FullName, b.FullName, StringComparison.Ordinal));
                            s_logger.Debug("AddDataTable Add Tag {}, name:{} record count:{}", tag, table.FullName, mainRecords.Count);
                        }
                    }
                }
            }
        }
    }

    public List<Record> GetTableAllDataList(DefTable table)
    {
        return _recordsByTables[table.FullName].FinalRecords;
    }

    public List<Record> GetTableExportDataList(DefTable table)
    {
        return _recordsByTables[table.FullName].FinalRecords;
    }

    public static List<Record> ToSortByKeyDataList(DefTable table, List<Record> originRecords)
    {
        var sortedRecords = new List<Record>(originRecords);

        DefField keyField = table.IndexField;
        if (keyField != null && (keyField.CType is TInt || keyField.CType is TLong))
        {
            string keyFieldName = keyField.Name;
            sortedRecords.Sort((a, b) =>
            {
                DType keya = a.Data.GetField(keyFieldName);
                DType keyb = b.Data.GetField(keyFieldName);
                switch (keya)
                {
                    case DInt ai:
                        return ai.Value.CompareTo((keyb as DInt).Value);
                    case DLong al:
                        return al.Value.CompareTo((keyb as DLong).Value);
                    default:
                        throw new NotSupportedException();
                }
            });
        }
        return sortedRecords;
    }

    public TableDataInfo GetTableDataInfo(DefTable table)
    {
        return _recordsByTables[table.FullName];
    }

    public ICodeStyle GetCodeStyle(string family)
    {
        if (EnvManager.Current.TryGetOption(family, BuiltinOptionNames.CodeStyle, true, out var codeStyleName))
        {
            return CodeFormatManager.Ins.GetCodeStyle(codeStyleName);
        }
        return null;
    }

    public object GetUniqueObject(string key)
    {
        lock (this)
        {
            return _uniqueObjects[key];
        }
    }

    public object TryGetUniqueObject(string key)
    {
        lock (this)
        {
            _uniqueObjects.TryGetValue(key, out var obj);
            return obj;
        }
    }

    public object GetOrAddUniqueObject(string key, Func<object> factory)
    {
        lock (this)
        {
            if (_uniqueObjects.TryGetValue(key, out var obj))
            {
                return obj;
            }
            else
            {
                obj = factory();
                _uniqueObjects.Add(key, obj);
                return obj;
            }
        }
    }

    public void LogValidatorFail(IDataValidator validator)
    {
        lock (this)
        {
            _failedValidatorTypes.Add(validator.GetType());
        }
    }

    public bool AnyValidatorFail
    {
        get
        {
            lock (this)
            {
                return _failedValidatorTypes.Count > 0;
            }
        }
    }
}
