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
using System.IO;
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
using NLog;

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

    // per-language (ContentHash, Stamp) 缓存，供 checksumconfig 注入与 l10n sidecar 写入共用（GetL10NLangStamps）
    private Dictionary<string, (string ContentHash, long Stamp)> _l10nLangStamps;

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
            _l10nLangStamps = null;
            TextProvider?.Load();
            DataLoaderManager.Ins.LoadDatas(this);

            // 为所有表计算校验和
            CalculateTableChecksums();

            _datasLoaded = true;
            s_logger.Info("load datas end");
        }
    }

    /// <summary>
    /// 为所有表计算内容指纹（全量数据 MD5，不过 group 过滤 -> c/s 同值）+ 内容版本戳 Stamp。
    /// Stamp 内容相关：读上次基准 sidecar，该表内容指纹没变 -> 沿用上次戳；变了 -> 本次批次时间。
    /// 仅普通表管线在此 gate（L10N 管线的 per-language 戳在 AddL10NLanguageChecksumRecords 算）。
    /// </summary>
    private void CalculateTableChecksums()
    {
        s_logger.Info("calculate table checksums begin");

        var batchTime = GetExportStamp();
        // 读上次基准里每张表的 (ContentHash, Stamp) 用于戳 gating：普通管线从 tables.json，L10N 管线从 l10n.json.Tables。
        var prevTables = LoadPreviousTableStamps();

        foreach (var table in Tables)
        {
            table.SignatureId = StructureSignature.ComputeForTable(table);

            if (!_recordsByTables.TryGetValue(table.FullName, out var tableDataInfo) ||
                tableDataInfo.FinalRecords == null ||
                tableDataInfo.FinalRecords.Count == 0)
            {
                s_logger.Debug("table {TableName} has no records, content hash empty", table.Name);
                table.ContentHash = "";
                table.Stamp = 0;
                continue;
            }

            try
            {
                // 使用二进制序列化计算内容指纹（全量数据 MD5）
                var bytes = new Serialization.ByteBuf();
                var records = tableDataInfo.FinalRecords;

                bytes.WriteSize(records.Count);
                foreach (var record in records)
                {
                    record.Data.Apply(new DataVisitors.BinaryChecksumVisitor(), bytes);
                }

                string contentHash = Utils.FileUtil.CalcMD5(bytes.CopyData());
                table.ContentHash = contentHash;
                table.Stamp = ResolveTableStamp(prevTables, table.FullName, contentHash, batchTime);

                s_logger.Debug("table {TableName} contentHash: {ContentHash} stamp: {Stamp} (records: {Count})",
                    table.Name, contentHash, table.Stamp, records.Count);
            }
            catch (Exception ex)
            {
                s_logger.Error(ex, "failed to calculate checksum for table {TableName}", table.Name);
                table.ContentHash = "";
                table.Stamp = batchTime;
            }
        }

        // 创建 Checksum 表的数据记录并添加到导出流程
        CreateChecksumData();

        s_logger.Info("calculate table checksums end");
    }

    /// <summary>
    /// 解析一张表的版本戳：上次基准里该表内容指纹没变（且上次戳有效 >0）-> 沿用上次戳；否则 -> 批次时间。
    /// 首次基准 / 内容变了 / 上次无戳 -> 推进到 batchTime。
    /// </summary>
    private static long ResolveTableStamp(Dictionary<string, (string ContentHash, long Stamp)> prevTables, string tableFullName, string contentHash, long batchTime)
    {
        if (prevTables != null &&
            prevTables.TryGetValue(tableFullName, out var prev) &&
            !string.IsNullOrEmpty(prev.ContentHash) && prev.ContentHash == contentHash &&
            prev.Stamp > 0)
        {
            return prev.Stamp;
        }
        return batchTime;
    }

    /// <summary>
    /// 读上次基准的 per-table (ContentHash, Stamp)。普通管线读 baseline/tables.json；L10N 管线读 baseline/l10n.json 的 Tables 段。
    /// 不存在/读失败 -> null（首次基准或 gate 失效，全部推进到 batchTime）。
    /// </summary>
    private Dictionary<string, (string ContentHash, long Stamp)> LoadPreviousTableStamps()
    {
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path) || !File.Exists(path))
        {
            return null;
        }
        try
        {
            if (L10NLanguages.Count > 0)
            {
                var l10n = Incremental.BaselineSidecarIO.LoadL10N(path);
                return l10n.Tables?.ToDictionary(kv => kv.Key, kv => (kv.Value.ContentHash, kv.Value.Stamp));
            }
            var b = Incremental.BaselineSidecarIO.Load(path);
            return b.Tables?.ToDictionary(kv => kv.Key, kv => (kv.Value.ContentHash, kv.Value.Stamp));
        }
        catch (Exception e)
        {
            s_logger.Warn(e, "failed to load previous baseline sidecar {Path}, stamp gating disabled", path);
            return null;
        }
    }

    /// <summary>
    /// 本次发布的批次时间戳（unix 秒）。读 incremental.exportStamp（脚本发布开始算一次，传给 client/server/lang 三次调用）；
    /// 未配置 -> 当前 unix 秒（仅单次调用自洽，跨调用需脚本传同一值）。
    /// </summary>
    public long GetExportStamp()
    {
        var s = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalExportStamp, true, "");
        if (long.TryParse(s, out var v) && v > 0)
        {
            return v;
        }
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    /// <summary>
    /// 创建虚拟的 Checksum 表定义（用于代码生成）
    /// </summary>
    private void CreateChecksumTableDef()
    {
        try
        {
            // 从 xargs 读取 checksum 输出文件名配置
            // 用法: -x checksumOutputFile=checksum  或在 conf 的 xargs 中配置
            string checksumOutputFile = EnvManager.Current.GetOptionOrDefault("", "checksumOutputFile", true, null);

            // 创建 Checksum 表定义
            var checksumTable = Checksum.ChecksumTableBuilder.CreateChecksumTableDef(Assembly, checksumOutputFile);

            if (checksumTable == null)
            {
                s_logger.Error("Failed to create checksum table definition");
                return;
            }

            // 将 Checksum 表添加到导出表列表，确保它会被生成代码
            if (ExportTables == null)
            {
                ExportTables = new List<DefTable>();
            }

            ExportTables.Add(checksumTable);

            s_logger.Info("created checksum table definition");
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "failed to create checksum table definition");
        }
    }

    /// <summary>
    /// 创建 Checksum 表的数据记录
    /// </summary>
    private void CreateChecksumData()
    {
        try
        {
            // 获取已创建的 Checksum 表
            var checksumTable = ExportTables.FirstOrDefault(t => t.Name == Checksum.ChecksumTableBuilder.ChecksumTableName);
            if (checksumTable == null)
            {
                s_logger.Error("{TableName} table not found in ExportTables", Checksum.ChecksumTableBuilder.ChecksumTableName);
                return;
            }

            // 创建 Checksum 数据记录
            var checksumRecords = Checksum.ChecksumTableBuilder.CreateChecksumRecords(checksumTable, Tables);

            // L10N 管线：追加 per-language 行（TableName=语言名，Checksum=整语言文件 MD5，SignatureId 共享）
            // 前端/服务器用现有 ChecksumConfig 类按语言名读取，做登录时 per-语言精准比对。
            if (L10NLanguages.Count > 0)
            {
                AddL10NLanguageChecksumRecords(checksumTable, checksumRecords);
            }

            if (checksumRecords.Count == 0)
            {
                s_logger.Warn("no checksum records to export");
                return;
            }

            // 将 Checksum 表添加到数据导出流程
            AddDataTable(checksumTable, checksumRecords, null);

            s_logger.Info("created checksum table data with {Count} records", checksumRecords.Count);
        }
        catch (Exception ex)
        {
            s_logger.Error(ex, "failed to create checksum table data");
        }
    }

    /// <summary>
    /// L10N 管线：把每种语言的版本戳作为 ChecksumConfig 的一行注入
    /// （TableName=语言名，Stamp=per-language 戳，SignatureId 共享）。
    /// 戳内容相关（gating 在上次 l10n sidecar 上），前端/服务器用现有 ChecksumConfig 类按语言名读取、比大小。
    /// </summary>
    private void AddL10NLanguageChecksumRecords(DefTable checksumTable, List<Record> checksumRecords)
    {
        // 共享 SignatureId：任取一张 L10N 表（全语言共享 Language bean 结构签名）
        string sharedSig = "";
        foreach (var t in Tables)
        {
            if (t.ValueTType is Types.TBean)
            {
                sharedSig = TypeVisitors.StructureSignature.ComputeForTable(t);
                break;
            }
        }

        foreach (var (lang, stampInfo) in GetL10NLangStamps())
        {
            checksumRecords.Add(Checksum.ChecksumTableBuilder.CreateChecksumRecord(checksumTable, lang, stampInfo.Stamp, sharedSig));
        }
    }

    /// <summary>
    /// per-language 版本戳（内容相关）：内容指纹没变 -> 沿用上次戳；变了 -> 批次时间。
    /// checksumconfig 注入（AddL10NLanguageChecksumRecords）与 l10n sidecar 写入（L10NBaselineWithSidecarExporter）共用，缓存避免重复算。
    /// </summary>
    public Dictionary<string, (string ContentHash, long Stamp)> GetL10NLangStamps()
    {
        if (_l10nLangStamps != null)
        {
            return _l10nLangStamps;
        }

        var batchTime = GetExportStamp();
        var prevPath = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        Incremental.L10NSidecar prev = null;
        if (!string.IsNullOrEmpty(prevPath) && File.Exists(prevPath))
        {
            try
            {
                prev = Incremental.BaselineSidecarIO.LoadL10N(prevPath);
            }
            catch (Exception e)
            {
                s_logger.Warn(e, "failed to load previous l10n sidecar {Path}, per-language stamp gating disabled", prevPath);
                prev = null;
            }
        }

        var contentHashes = Incremental.L10NChecksumUtil.ComputePerLanguageFileMd5(this, L10NLanguages, L10NTextKeyFieldName);
        var result = new Dictionary<string, (string, long)>(contentHashes.Count);
        foreach (var (lang, hash) in contentHashes)
        {
            long stamp = batchTime;
            if (prev != null && prev.Languages.TryGetValue(lang, out var prevLang)
                && !string.IsNullOrEmpty(prevLang.ContentHash) && prevLang.ContentHash == hash && prevLang.Stamp > 0)
            {
                stamp = prevLang.Stamp;
            }
            result[lang] = (hash, stamp);
        }
        _l10nLangStamps = result;
        return result;
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
        if (Assembly.ExportTables == null)
        {
            ExportTables = new List<DefTable>();
        }
        else
        {
            ExportTables = Assembly.ExportTables
                .OrderBy(t => t.Name)
                .ToList();
        }

        // 创建虚拟的 Checksum 表定义（必须在 CalculateExportTypes 之前）
        CreateChecksumTableDef();

        ExportTypes = CalculateExportTypes();
        ExportBeans = SortBeanTypes(ExportTypes.OfType<DefBean>().ToList());
        ExportEnums = ExportTypes.OfType<DefEnum>().ToList();
    }

    public (IReadOnlyList<L10NKeyInfo>, System.Type) GetL10NKeyInfos()
    {
        if (_l10nKeyInfos != null)
        {
            return (_l10nKeyInfos, typeof(int));
        }

        var (keys, keyType) = EnumerateL10NKeys(ExportTables);
        _l10nKeyInfos = keys;
        return (_l10nKeyInfos, keyType);
    }

    /// <summary>
    /// 仅枚举指定表集合的 l10n key（不写缓存）。
    /// 用于代码生成时只从“代码引用语言表”取 key，而非全部语言表。
    /// </summary>
    public (IReadOnlyList<L10NKeyInfo>, System.Type) GetL10NKeyInfos(IReadOnlyList<DefTable> tables)
    {
        if (!DatasLoaded || L10NLanguages.Count == 0)
        {
            return (Array.Empty<L10NKeyInfo>(), typeof(int));
        }
        return EnumerateL10NKeys(tables);
    }

    private (List<L10NKeyInfo>, System.Type) EnumerateL10NKeys(IReadOnlyList<DefTable> tables)
    {
        if (!DatasLoaded || L10NLanguages.Count == 0)
        {
            return (new List<L10NKeyInfo>(), typeof(int));
        }

        var keyFieldName = L10NTextKeyFieldName;
        var keyFieldDesc = L10NTextKeyFieldDesc;
        var langSet = new HashSet<string>(L10NLanguages, StringComparer.Ordinal);
        var keys = new List<(object, string)>();
        var keySet = new HashSet<object>();
        System.Type keyType = null;

        foreach (var table in tables)
        {
            if (table.ValueTType is not TBean tbean)
            {
                continue;
            }

            var bean = tbean.DefBean;
            if (!HasAnyLanguageField(bean, langSet))
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
                var keyValue = data.GetField(keyFieldName);
                if (keyType == null) keyType = keyValue.GetValueObject().GetType();
                if (keyValue is DString stringValue)
                {
                    if (string.IsNullOrEmpty(stringValue.Value))
                    {
                        continue;
                    }
                }

                if (keySet.Add(keyValue.GetValueObject()))
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
                    keys.Add((keyValue.GetValueObject(), descContent));
                }
            }
        }

        //keys.Sort((v1, v2) => String.Compare(v1.Item1, v2.Item1, StringComparison.Ordinal));
        return (BuildL10NKeyInfos(keys), keyType);
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

    private static List<L10NKeyInfo> BuildL10NKeyInfos(IEnumerable<(object, string)> keys)
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
            s_logger.Debug("keys add {}, {}, {}", key.Item1, fieldName, key.Item2);    
            result.Add(new L10NKeyInfo(key.Item1, fieldName, key.Item2));
        }
        return result;
    }

    private static string MakeIdentifier(object key)
    {
        var sb = new StringBuilder();
        foreach (char ch in key.ToString())
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
            if (table == null)
            {
                continue;
            }

            refTypes[table.FullName] = table;

            if (table.ValueTType != null)
            {
                table.ValueTType.Apply(RefTypeVisitor.Ins, refTypes);
            }
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
        // 只有当表的 Group 符合当前 Target 的 Group 时，才将其加入 _tablesByTag
        if (AllTags != null && AllTags.Count > 0 && tableDataInfo.FinalRecords != null && tableDataInfo.FinalRecords.Count > 0)
        {
            // 检查表是否应该被当前 Target 导出（根据 Group 过滤）
            bool shouldExport = Assembly.NeedExport(table.Groups, GlobalConf.Groups);
            if (!shouldExport)
            {
                s_logger.Debug("AddDataTable Skip table {} (Group mismatch with Target Groups: {})", table.FullName, string.Join(",", Target.Groups));
                return;
            }

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
