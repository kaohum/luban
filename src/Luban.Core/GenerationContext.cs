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

    // per-space per-language (ContentHash, Stamp) 缓存（GetL10NSpaceLangStamps），sidecar 与 space checksum 行共用
    private readonly Dictionary<string, Dictionary<string, (string ContentHash, long Stamp)>> _l10nSpaceLangStamps = new();

    // 配置 spaces 时的 per-space checksum 行（space 名 -> 语言行记录，TableName=语言名）。
    // 根目录 checksumconfig 只含普通表行；各 space 的语言行由 omnibus-baseline 写到 {space.OutputDir}/checksumconfig.bytes。
    private readonly Dictionary<string, List<Record>> _l10nSpaceChecksumRecords = new();

    public IReadOnlyDictionary<string, List<Record>> L10NSpaceChecksumRecords => _l10nSpaceChecksumRecords;

    // dataExporter 名称（例如 default、l10n-bin-split），供模板感知当前导出模式
    public string DataExporterName { get; private set; }

    // l10n 相关配置，便于模板直接复用
    public IReadOnlyList<string> L10NLanguages { get; private set; } = Array.Empty<string>();

    public string L10NTextKeyFieldName { get; private set; }
    public string L10NTextKeyFieldDesc { get; private set; }

    // 多 space l10n 配置(l10n.spaces);未配置时空表,走旧单值选项路径
    public IReadOnlyList<L10N.L10NSpace> L10NSpaces { get; private set; } = Array.Empty<L10N.L10NSpace>();

    // 任一 space 开启 indexMode 即视为启用 text 转 int 下标模式
    public bool L10NTextIndexEnabled { get; private set; }

    // ExportBeans 是否含 text 标签字段(递归,含父类/子类/容器);懒计算并缓存,
    // 供各代码目标 ValidateDefinition 的共享 WARN 每目标只扫描一次
    private bool? _hasTextTaggedExportField;

    /// <summary>导出 bean(含父类/子类/容器/内嵌 bean 递归)中是否存在 text 标签的 string 字段。</summary>
    public bool HasTextTaggedExportField
    {
        get
        {
            _hasTextTaggedExportField ??= L10N.TextFieldDetector.HasAnyTextField(ExportBeans);
            return _hasTextTaggedExportField.Value;
        }
    }

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
            _l10nSpaceLangStamps.Clear();
            _l10nSpaceChecksumRecords.Clear();
            TextProvider?.Load();
            DataLoaderManager.Ins.LoadDatas(this);

            // L10N 下标注册表构建 + text 字段 → DInt(必须在 checksum 之前,保证行哈希基于 int)
            L10NKeyIndexBuilder.Build(this);

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
    /// 配置 spaces 时，space 表的 gating 状态在各 space 自己的 sidecar（普通 baseline sidecar 按 TableFilter 排除了 space 表），
    /// 因此额外合并各 space sidecar 的 Tables 段（同文件不重读；主 sidecar 优先，space 条目不覆盖）。
    /// 未配置 spaces 时行为与原先完全一致（只读主 sidecar 一份）。
    /// </summary>
    private Dictionary<string, (string ContentHash, long Stamp)> LoadPreviousTableStamps()
    {
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        var result = ReadPrevTableStamps(path);
        if (L10NSpaces.Count > 0)
        {
            foreach (var space in L10NSpaces)
            {
                if (string.IsNullOrEmpty(space.SidecarPath))
                {
                    continue; // 该 space 未配置 sidecar
                }
                if (string.Equals(space.SidecarPath, path, StringComparison.OrdinalIgnoreCase))
                {
                    // 同文件：普通表 baseline sidecar 与该 space 的 L10N sidecar 互相覆盖对方的 Tables 段
                    //（后写者胜，space sidecar 通常最后写）。症状是根 checksumconfig 里内容未变的普通表 Stamp 每轮推进
                    //（客户端重复下载未变化的表，gating 静默失效）。仅告警不改变行为；建议两者配置不同文件。
                    s_logger.Warn(
                        "incremental.sidecarPath 与 space {Space} 的 l10n.{Space}.sidecar 指向同一文件 ({Path})：" +
                        "两种 sidecar 会互相覆盖对方的 Tables 段，普通表/space 表的表级 Stamp gating 状态丢失，" +
                        "checksumconfig 中内容未变的表戳仍会每轮推进。建议改用不同的 sidecar 文件。",
                        space.Name, space.Name, path);
                    continue; // 与主 sidecar 同文件（已读过），不重读
                }
                var spaceStamps = ReadPrevTableStamps(space.SidecarPath);
                if (spaceStamps == null)
                {
                    continue;
                }
                result ??= new Dictionary<string, (string ContentHash, long Stamp)>();
                foreach (var kv in spaceStamps)
                {
                    result.TryAdd(kv.Key, kv.Value);
                }
            }
        }
        return result;
    }

    /// <summary>读单个 sidecar 文件的 per-table (ContentHash, Stamp)；不存在/读失败返回 null。</summary>
    private Dictionary<string, (string ContentHash, long Stamp)> ReadPrevTableStamps(string path)
    {
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
            // 配置了 spaces：根 checksumconfig 只含普通表行（排除 space 表，无语言行）；
            // 各 space 的 per-language 行（TableName=语言名）拆到 L10NSpaceChecksumRecords，由 omnibus-baseline
            // 在各 space.OutputDir 下输出独立 checksumconfig.bytes。
            IEnumerable<DefTable> checksumSourceTables = L10NSpaces.Count > 0 ? Tables.Where(t => !IsSpaceTable(t)) : Tables;
            var checksumRecords = Checksum.ChecksumTableBuilder.CreateChecksumRecords(checksumTable, checksumSourceTables);

            if (L10NSpaces.Count > 0)
            {
                BuildL10NSpaceChecksumRecords(checksumTable);
            }
            else if (L10NLanguages.Count > 0)
            {
                // L10N 管线（旧单值选项路径）：追加 per-language 行（TableName=语言名），行为与原先完全一致
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

    /// <summary>表是否属于任一 space 名单（space.Tables 记表全名或短名）。</summary>
    private bool IsSpaceTable(DefTable table)
    {
        foreach (var space in L10NSpaces)
        {
            if (space.Tables.Contains(table.Name) || space.Tables.Contains(table.FullName))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 配置 spaces 时构建各 space 的 checksum 语言行（TableName=语言名，Stamp=per-language 戳，SignatureId 共享）。
    /// 行存入 L10NSpaceChecksumRecords，根 checksumRecords 不掺语言行。
    /// </summary>
    private void BuildL10NSpaceChecksumRecords(DefTable checksumTable)
    {
        foreach (var space in L10NSpaces)
        {
            try
            {
                if (space.Languages.Count == 0)
                {
                    continue;
                }

                // 共享 SignatureId：space 名单内任取一张语言表（全语言共享 Language bean 结构签名）
                string sharedSig = "";
                foreach (var t in L10NKeyIndexBuilder.MatchTables(this, space))
                {
                    if (t.ValueTType is Types.TBean)
                    {
                        sharedSig = TypeVisitors.StructureSignature.ComputeForTable(t);
                        break;
                    }
                }

                var records = new List<Record>(space.Languages.Count);
                foreach (var (lang, stampInfo) in GetL10NSpaceLangStamps(space))
                {
                    records.Add(Checksum.ChecksumTableBuilder.CreateChecksumRecord(checksumTable, lang, stampInfo.Stamp, sharedSig));
                }
                _l10nSpaceChecksumRecords[space.Name] = records;
            }
            catch (Exception e)
            {
                // 单个 space 失败不阻断根 checksum 与其它 space
                s_logger.Error(e, "failed to build l10n checksum records for space {Space}", space.Name);
            }
        }
    }

    /// <summary>
    /// space 限定版 per-language 版本戳（内容相关）：内容指纹没变 -> 沿用上次戳；变了 -> 批次时间。
    /// indexMode space 的指纹按数组序列化（与 {outputDir}/{lang}/{outputFile}.bytes 一致）计算，
    /// 非 indexMode space 按 string 字典序列化（与逐表 {lang}/{file}.bytes 合并布局一致）。
    /// gating 读该 space 自己的 sidecar（space.SidecarPath）；结果缓存，供 space checksumconfig 行与
    /// sidecar（WriteIndexSpaceSidecar / WriteLegacySpaceSidecar）共用同一份值。
    /// </summary>
    public Dictionary<string, (string ContentHash, long Stamp)> GetL10NSpaceLangStamps(L10N.L10NSpace space)
    {
        if (_l10nSpaceLangStamps.TryGetValue(space.Name, out var cached))
        {
            return cached;
        }

        var batchTime = GetExportStamp();
        Incremental.L10NSidecar prev = null;
        if (!string.IsNullOrEmpty(space.SidecarPath) && File.Exists(space.SidecarPath))
        {
            try
            {
                prev = Incremental.BaselineSidecarIO.LoadL10N(space.SidecarPath);
            }
            catch (Exception e)
            {
                s_logger.Warn(e, "failed to load previous l10n sidecar {Path} of space {Space}, per-language stamp gating disabled", space.SidecarPath, space.Name);
                prev = null;
            }
        }

        var contentHashes = Incremental.L10NChecksumUtil.ComputePerLanguageFileMd5(this, space);
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
        _l10nSpaceLangStamps[space.Name] = result;
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

        // 多 space 配置(l10n.spaces);未配置时空表,走旧单值选项路径
        L10NSpaces = L10NSpaceParser.Parse(CollectL10NOptions());
        L10NTextIndexEnabled = L10NSpaces.Any(s => s.IndexMode);

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

        // 提前算结构签名：代码生成(cs-bin 的 ExpectedSignatureId const)需要它，必须在代码生成之前就绪。
        // 早于数据加载阶段(CalculateTableChecksums 也会算并复用此处结果)。
        foreach (var table in ExportTables)
        {
            table.SignatureId = StructureSignature.ComputeForTable(table);
        }
    }

    /// <summary>
    /// 从 EnvManager 收集 l10n 前缀的选项为扁平字典,供 L10NSpaceParser 解析。
    /// EnvManager 的选项本身就是按完整 key("-x l10n.main.tables=x" -> "l10n.main.tables")存放的扁平字典,
    /// 因此 family 传空串、name 传完整 key 即可精确命中(与 L10NOptionUtil 的 "l10n"+".key 拼接等价)。
    /// </summary>
    private Dictionary<string, string> CollectL10NOptions()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var env = EnvManager.Current;
        string Probe(string key)
        {
            var v = env.GetOptionOrDefault("", key, true, "");
            return v ?? "";
        }
        dict["l10n.spaces"] = Probe("l10n.spaces");
        foreach (var name in Probe("l10n.spaces").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            foreach (var k in new[] { "tables", "indexMode", "languages", "outputFile", "outputDir", "keyFieldName", "keyFieldDesc", "nameFieldName", "genAccessors", "sidecar" })
            {
                dict[$"l10n.{name}.{k}"] = Probe($"l10n.{name}.{k}");
            }
        }
        return dict;
    }

    public IReadOnlyList<L10NKeyInfo> GetL10NKeyInfos()
    {
        if (_l10nKeyInfos != null)
        {
            return _l10nKeyInfos;
        }

        _l10nKeyInfos = EnumerateL10NKeys(ExportTables);
        return _l10nKeyInfos;
    }

    /// <summary>
    /// 仅枚举指定表集合的 l10n key（不写缓存）。
    /// 用于代码生成时只从“代码引用语言表”取 key，而非全部语言表。
    /// flagFieldName 非空时，进一步按行内 bool 标记字段过滤：仅该字段为 true 的行入选。
    /// languages/keyFieldName/keyFieldDesc/nameFieldName 用于 space 感知的调用方（如 cs-l10n-language 多 space 生成）
    /// 按 space 自己的语言名单与 key/desc/name 字段枚举；传 null 时沿用全局选项（旧调用方行为不变）。
    /// </summary>
    public IReadOnlyList<L10NKeyInfo> GetL10NKeyInfos(IReadOnlyList<DefTable> tables, string flagFieldName = null,
        IReadOnlyList<string> languages = null, string keyFieldName = null, string keyFieldDesc = null, string nameFieldName = null)
    {
        var langList = languages ?? L10NLanguages;
        if (!DatasLoaded || langList.Count == 0)
        {
            return Array.Empty<L10NKeyInfo>();
        }
        return EnumerateL10NKeys(tables, flagFieldName, langList, keyFieldName, keyFieldDesc, nameFieldName);
    }

    /// <summary>
    /// v2（spec 2026-08-22 D7）：key 字段（id 列，int）→ <see cref="L10NKeyInfo.Id"/>（烘进 Get(id)）；
    /// name 列（string，可选）→ 访问器名 <see cref="L10NKeyInfo.FieldName"/>，name 为空（含表无该列）时退化为 L_{id} 派生；
    /// desc 字段 → XML 注释内容。旧行为里 id 为 string key（未配 spaces 的单值路径）时 Id 导出 -1（过渡形态）。
    /// </summary>
    private List<L10NKeyInfo> EnumerateL10NKeys(IReadOnlyList<DefTable> tables, string flagFieldName = null,
        IReadOnlyList<string> languages = null, string keyFieldName = null, string keyFieldDesc = null, string nameFieldName = null)
    {
        var langList = languages ?? L10NLanguages;
        if (!DatasLoaded || langList.Count == 0)
        {
            return new List<L10NKeyInfo>();
        }

        bool hasFlagFilter = !string.IsNullOrWhiteSpace(flagFieldName);
        int flagFieldMatchedTables = 0;

        var keyFieldNameResolved = string.IsNullOrWhiteSpace(keyFieldName) ? L10NTextKeyFieldName : keyFieldName;
        var keyFieldDescResolved = string.IsNullOrWhiteSpace(keyFieldDesc) ? L10NTextKeyFieldDesc : keyFieldDesc;
        var nameFieldNameResolved = string.IsNullOrWhiteSpace(nameFieldName) ? "name" : nameFieldName;
        var langSet = new HashSet<string>(langList, StringComparer.Ordinal);
        var keys = new List<(object, string, string)>();
        var keySet = new HashSet<object>();

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

            // 行级标记字段过滤：仅收录标记字段为 true 的行（字段缺失的表不贡献 key）
            DefField flagField = null;
            if (hasFlagFilter)
            {
                flagField = bean.Fields.FirstOrDefault(f => string.Equals(f.Name, flagFieldName, StringComparison.Ordinal));
                if (flagField == null)
                {
                    continue;
                }
                if (flagField.CType is not TBool)
                {
                    throw new Exception($"[l10n] 表 {table.FullName} 的标记字段 '{flagFieldName}' 类型必须是 bool，当前为 {flagField.CType.TypeName}");
                }
                flagFieldMatchedTables++;
            }

            if (!_recordsByTables.TryGetValue(table.FullName, out var tableDataInfo) || tableDataInfo.FinalRecords == null)
            {
                continue;
            }

            var hasDesc = HasStringField(bean, keyFieldDescResolved);
            var hasName = HasStringField(bean, nameFieldNameResolved);

            foreach (var record in tableDataInfo.FinalRecords)
            {
                if (record.Data is not DBean data)
                {
                    continue;
                }
                if (flagField != null && data.GetField(flagField.Name) is not DBool { Value: true })
                {
                    continue;
                }
                var keyValue = data.GetField(keyFieldNameResolved);
                if (keyValue == null)
                {
                    continue;
                }
                if (keyValue is DString stringValue)
                {
                    if (string.IsNullOrEmpty(stringValue.Value))
                    {
                        continue;
                    }
                }

                if (keySet.Add(keyValue.GetValueObject()))
                {
                    var nameContent = string.Empty;
                    if (hasName)
                    {
                        if (data.GetField(nameFieldNameResolved) is DString nameValue && !string.IsNullOrWhiteSpace(nameValue.Value))
                        {
                            nameContent = nameValue.Value;
                        }
                        else
                        {
                            // 表有 name 列但该行为空：访问器名退化为 L_{id} 派生，WARN 提示补全（表无 name 列属正常形态，不告警）
                            s_logger.Warn("[l10n] 表 {table} key {key} 的 name 列为空,访问器名退化为 L_{{id}} 派生,建议补全 name", table.FullName, keyValue.GetValueObject());
                        }
                    }
                    var descContent = string.Empty;
                    if (hasDesc)
                    {
                        var descValue = data.GetField(keyFieldDescResolved) as DString;
                        if (descValue != null && !string.IsNullOrEmpty(descValue.Value))
                        {
                            descContent = descValue.Value;
                        }
                    }
                    keys.Add((keyValue.GetValueObject(), nameContent, descContent));
                }
            }
        }

        if (hasFlagFilter && flagFieldMatchedTables == 0)
        {
            var available = string.Join(", ", tables.Select(t => t.FullName));
            throw new Exception(
                $"[l10n] 标记字段 '{flagFieldName}' 未在任何多语言表中定义。" +
                $"请检查 language schema 中 Language bean 是否包含该 bool 字段。当前表集合：{available}");
        }

        return BuildL10NKeyInfos(keys);
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

    private static List<L10NKeyInfo> BuildL10NKeyInfos(IEnumerable<(object Key, string Name, string Desc)> keys)
    {
        var result = new List<L10NKeyInfo>();
        var nameCount = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var key in keys)
        {
            // 访问器名优先取 name 列（v2 D7）；name 为空（空单元格或表无 name 列）退化为 L_{id} 派生标识符
            string identifierSource = string.IsNullOrWhiteSpace(key.Name) ? "L_" + key.Key : key.Name;
            string fieldName = MakeIdentifier(identifierSource);
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
            s_logger.Debug("keys add id:{}, name:{}, field:{}, desc:{}", ToL10NKeyId(key.Key), key.Name, fieldName, key.Desc);
            result.Add(new L10NKeyInfo(ToL10NKeyId(key.Key),
                string.IsNullOrWhiteSpace(key.Name) ? null : key.Name, fieldName, key.Desc));
        }
        return result;
    }

    /// <summary>key 值（id 列）→ 烘焙用 int id：int 直接取，其余整数形态收窄为 int，非整数（旧 string key）返回 -1。</summary>
    private static int ToL10NKeyId(object key)
    {
        return key switch
        {
            int i => i,
            long l => unchecked((int)l),
            short s => s,
            byte b => b,
            _ => -1,
        };
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
