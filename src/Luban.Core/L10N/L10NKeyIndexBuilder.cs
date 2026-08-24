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

using System;
using System.Collections.Generic;
using System.Linq;
using Luban.Datas;
using Luban.Defs;
using Luban.Incremental;
using Luban.Types;
using Luban.Utils;

namespace Luban.L10N
{
    /// <summary>
    /// LoadDatas 末尾:为每个 indexMode space 构建显式 int 语言 id 注册表,并把业务表 text 字段(int 字面量)改写为 DInt(id)。
    /// 必须在 CalculateTableChecksums 之前执行(checksum 直接基于 int 数据,id 在表内,天然稳定幂等)。
    /// v2(spec 2026-08-22):语言 id 为策划显式填写的 int,不再导表期自动分配下标(KeyIndexAllocator 已退役);
    /// 注册表 = 语言表 id 列(活 id 集)∪ sidecar 墓碑差集(墓碑仅供增量 diff,不参与 text 校验)。
    /// 静态检查:语言表重复 id=硬错误;id 跨段(对照 spec §2 段表)=仅告警。
    /// </summary>
    public static class L10NKeyIndexBuilder
    {
        private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

        // spec §2 段首常量表(docs/superpowers/specs/2026-08-22-l10n-explicit-int-id-design.md):
        // 10000 GlobalCommon / 20000 Building / 30000 BattleGrowth / 40000 GameplayQuest /
        // 50000 WorldMapMarch / 60000 AllianceSocial / 70000 StoryGuide / 80000 ItemMail。
        // 90000 为预留段(段满顺延,导出告警留痕);>=100000 为十万级扩展区(含 AOT 100000-199999),整区视为同段。
        // 每个语言表文件归属一个段;同文件内出现他段 id = 误填,仅告警不阻塞。
        private static readonly int[] s_segmentStarts = { 10000, 20000, 30000, 40000, 50000, 60000, 70000, 80000 };

        private const int ExtensionZoneStart = 100000;

        /// <summary>无段 id(低于最小段首 10000)的段值。</summary>
        private const int NoSegment = 0;

        public static void Build(GenerationContext ctx)
        {
            if (ctx.L10NSpaces.Count == 0)
            {
                return;
            }
            // indexMode 与 DefaultTextProvider 的 convertTextKeyToValue(text 字段 string→value 替换)互斥:
            // 两者都要改写 text 字段且语义冲突(int id vs 语言值),同时开启直接报错。
            // 选项 key 与 DefaultTextProvider.Load 的读取方式保持一致(l10n.convertTextKeyToValue)。
            if (ctx.L10NTextIndexEnabled && DataUtil.ParseBool(EnvManager.Current.GetOptionOrDefault(
                    BuiltinOptionNames.L10NFamily, BuiltinOptionNames.L10NConvertTextKeyToValue, false, "false")))
            {
                throw new TextKeyIndexException(
                    "[lan-index] l10n.indexMode 与 l10n.convertTextKeyToValue=true 互斥:text 字段无法同时做 string→value 替换与 int id 转换,请只开启其一");
            }
            foreach (var space in ctx.L10NSpaces.Where(s => s.IndexMode))
            {
                var currentIds = CollectCurrentIds(ctx, space); // 重复 id 硬错误;跨段/段外 id 仅告警
                space.KeyIndex = BuildRegistry(currentIds, LoadPrevIds(space.SidecarPath));
            }
            if (!ctx.L10NTextIndexEnabled)
            {
                return;
            }
            TransformTextFields(ctx);
        }

        /// <summary>
        /// 语言表当前 id 集(读 id 列的 DInt;space.Tables 名单内的表,跨表并集)。
        /// 重复 id(同表或跨表)→ 硬错误;按源文件分组做跨段检查 → 仅告警。
        /// </summary>
        private static HashSet<int> CollectCurrentIds(GenerationContext ctx, L10NSpace space)
        {
            var rows = new List<(int Id, string Origin)>();
            foreach (var table in MatchTables(ctx, space))
            {
                if (table.ValueTType is not TBean tbean)
                {
                    throw new TextKeyIndexException(
                        $"[lan-index] space '{space.Name}' 的语言表 {table.FullName} value 不是 bean,无法读取 id 列 '{space.KeyFieldName}'");
                }
                var idField = tbean.DefBean.Fields.FirstOrDefault(f =>
                    string.Equals(f.Name, space.KeyFieldName, StringComparison.Ordinal));
                if (idField == null)
                {
                    throw new TextKeyIndexException(
                        $"[lan-index] space '{space.Name}' 的语言表 {table.FullName} 缺少 id 字段 '{space.KeyFieldName}'(v2 语言表需要显式 int id 列 + name string 列)");
                }
                if (idField.CType is not TInt)
                {
                    throw new TextKeyIndexException(
                        $"[lan-index] space '{space.Name}' 的语言表 {table.FullName} 的 id 字段 '{space.KeyFieldName}' 必须是 int(v2 显式语言 id),当前为 {idField.CType}");
                }
                // 跨段检查按源文件分组:一个语言表 def 可挂多个输入文件,每个文件归属一个段
                var fileIds = new Dictionary<string, List<int>>(StringComparer.Ordinal);
                foreach (var rec in ctx.GetTableExportDataList(table))
                {
                    if (rec.Data is not DBean bean)
                    {
                        continue;
                    }
                    if (bean.GetField(space.KeyFieldName) is not DInt d || d.Value <= 0)
                    {
                        continue; // 空行/无 id 行跳过(id 是表 index,正常必有值)
                    }
                    rows.Add((d.Value, $"{table.Name}[{rec.AutoIndex}]"));
                    var source = rec.Source ?? table.FullName;
                    if (!fileIds.TryGetValue(source, out var ids))
                    {
                        ids = new List<int>();
                        fileIds[source] = ids;
                    }
                    ids.Add(d.Value);
                }
                foreach (var (file, ids) in fileIds)
                {
                    WarnCrossSegment(space.Name, table.FullName, file, ids);
                }
            }
            return MergeRowIds(rows, space.Name);
        }

        /// <summary>把 (id, 行来源) 并入 space id 集;任一 id 重复 → 硬错误(导出中止)。</summary>
        public static HashSet<int> MergeRowIds(IEnumerable<(int Id, string Origin)> rows, string spaceName)
        {
            var ids = new HashSet<int>();
            foreach (var (id, origin) in rows)
            {
                if (!ids.Add(id))
                {
                    throw new TextKeyIndexException(
                        $"[lan-index] space '{spaceName}' 存在重复语言 id {id}(行 {origin}):显式 id 必须全 space 唯一,请修正语言表");
                }
            }
            return ids;
        }

        /// <summary>
        /// 注册表 = 表内活 id ∪ sidecar 墓碑差集(prev - current);表内 id 优先(复活)。
        /// 幂等:id 在表内,同一 (current, prev) 输入恒定产出同一注册表。
        /// </summary>
        public static L10NKeyIndex BuildRegistry(HashSet<int> currentIds, IEnumerable<int> prevIds)
        {
            return new L10NKeyIndex(currentIds, prevIds ?? Array.Empty<int>());
        }

        /// <summary>
        /// 读上次 sidecar 记录过的 id 集(活+墓碑一并视作"曾出现",v2 KeyEntry.Id 直接为 int;
        /// Id<=0 的条目是 v1 string 格式残留反序列化出的垃圾值,忽略)。文件不存在/读取失败返回空集
        /// (等价于首次基准);v1 string-key sidecar 在 BaselineSidecarIO.LoadL10N 处整体视为空。
        /// </summary>
        public static HashSet<int> LoadPrevIds(string sidecarPath)
        {
            if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
            {
                return new HashSet<int>();
            }
            try
            {
                var sidecar = BaselineSidecarIO.LoadL10N(sidecarPath);
                var result = new HashSet<int>();
                foreach (var e in sidecar?.KeyEntries ?? (IReadOnlyList<KeyEntry>)Array.Empty<KeyEntry>())
                {
                    if (e.Id > 0)
                    {
                        result.Add(e.Id);
                    }
                }
                return result;
            }
            catch (Exception e)
            {
                s_logger.Warn(e, "failed to load l10n sidecar key entries from {Path}, treating as empty registry (no tombstones)", sidecarPath);
                return new HashSet<int>();
            }
        }

        /// <summary>id 所属段:万级分段 id/10000*10000;&lt;10000 不属于任何段;十万级扩展区(含 AOT)整区同段。</summary>
        public static int GetSegment(int id)
        {
            if (id < s_segmentStarts[0])
            {
                return NoSegment;
            }
            if (id >= ExtensionZoneStart)
            {
                return ExtensionZoneStart;
            }
            return id / 10000 * 10000;
        }

        /// <summary>
        /// 文件的分配段锚:该文件最小合法 id(≥10000)所在段;全文件无合法段 id 时返回无段(全部按段外告警)。
        /// </summary>
        private static int GetAnchorSegment(IReadOnlyList<int> fileIds)
        {
            var anchorCandidates = fileIds.Where(id => GetSegment(id) != NoSegment).ToList();
            return anchorCandidates.Count > 0 ? GetSegment(anchorCandidates.Min()) : NoSegment;
        }

        /// <summary>
        /// 单个语言表文件内的跨段 id 集合:以该文件最小合法 id 的段为锚(文件的分配段),
        /// 其余段的 id 均为跨段;自身段为无段(&lt;10000)的 id 无论锚段为何一律跨段;
        /// 锚段为无段(全文件无合法段 id)时,文件内所有 id 均按段外告警。扩展区(&gt;=100000)内视为同段。
        /// </summary>
        public static List<int> FindCrossSegmentIds(IReadOnlyList<int> fileIds)
        {
            if (fileIds == null || fileIds.Count == 0)
            {
                return new List<int>();
            }
            int anchor = GetAnchorSegment(fileIds);
            // 锚段为无段(全文件无 ≥10000 的合法段 id)→ 文件内所有 id 一律按段外告警;
            // 锚段合法 → 段外 id(自身段为无段)与其余段的 id 均为跨段。
            return fileIds.Where(id => anchor == NoSegment || GetSegment(id) != anchor).ToList();
        }

        private static void WarnCrossSegment(string spaceName, string tableFullName, string file, IReadOnlyList<int> fileIds)
        {
            var foreign = FindCrossSegmentIds(fileIds);
            if (foreign.Count == 0)
            {
                return;
            }
            s_logger.Warn(
                "[lan-index][cross-segment] space '{Space}' 表 {Table} 文件 {File} 存在跨段 id: {Ids}(该文件分配段 {Anchor},spec §2 段表;跨段仅告警,请确认是否误填)",
                spaceName, tableFullName, file, string.Join(", ", foreign), GetAnchorSegment(fileIds));
        }

        /// <summary>业务表 text 字段(int 字面量)→ DInt(id)。引用 space 由 l10n.textRefSpace 指定(默认 main)。</summary>
        private static void TransformTextFields(GenerationContext ctx)
        {
            string refSpaceName = EnvManager.Current.GetOptionOrDefault("l10n", "textRefSpace", true, "main");
            var refSpace = ctx.L10NSpaces.FirstOrDefault(s =>
                string.Equals(s.Name, refSpaceName, StringComparison.Ordinal) && s.IndexMode);
            if (refSpace?.KeyIndex == null)
            {
                throw new TextKeyIndexException(
                    $"[lan-index] l10n.textRefSpace='{refSpaceName}' 未命中任何 indexMode=true 的 space,无法解析 text 字段");
            }
            var liveIds = refSpace.KeyIndex.LiveIds; // 静态校验集 = 语言表活 id(墓碑 id 不算,删行后的引用按缺失告警)

            var spaceTableNames = new HashSet<string>(ctx.L10NSpaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
            var missingEntries = new List<MissingTextIdEntry>();
            foreach (var table in ctx.Tables)
            {
                if (spaceTableNames.Contains(table.Name) || spaceTableNames.Contains(table.FullName))
                {
                    continue; // 语言表自身的 id 字段不转
                }
                // text 字段做表 key/index:转换后 key 变 DInt,与声明的 string key 类型必然错位,提前报错
                foreach (var indexInfo in table.IndexList)
                {
                    foreach (var indexField in indexInfo.IndexFields)
                    {
                        if (indexField.CType is TString indexText && indexText.HasTag("text"))
                        {
                            throw new TextKeyIndexException(
                                $"[lan-index] 表 {table.FullName} 的 index 字段 '{indexField.Name}' 为 text 类型,不能做表 key/index:text 字段在 indexMode 下会被转成 int 语言 id,请改用普通 string 或 int 字段");
                        }
                    }
                }
                foreach (var record in ctx.GetTableExportDataList(table))
                {
                    if (record.Data is not DBean bean)
                    {
                        continue;
                    }
                    var trans = new TextKeyIndexTransformer(liveIds, table.FullName, "", BuildRowId(table, record), missingEntries);
                    record.Data = (DBean)record.Data.Apply(trans, table.ValueTType);
                }
            }
            WriteMissingIdsReport(missingEntries);
        }

        /// <summary>
        /// 报告/告警用的行标识:主索引(IndexList[0])字段值,组合索引以 '+' 连接;
        /// 无索引(ONE 表)或值缺失时退化为物理行号(AutoIndex)。与 sidecar 行键语义同源。
        /// </summary>
        internal static string BuildRowId(DefTable table, Record record)
        {
            if (record.Data is DBean bean && table.IndexList.Count > 0)
            {
                var primary = table.IndexList[0];
                var parts = new List<string>(primary.IndexFieldIdIndexes.Count);
                foreach (var idx in primary.IndexFieldIdIndexes)
                {
                    var value = idx >= 0 && idx < bean.Fields.Count ? bean.Fields[idx] : null;
                    if (value == null)
                    {
                        return record.AutoIndex.ToString();
                    }
                    parts.Add(value.ToString());
                }
                return string.Join("+", parts);
            }
            return record.AutoIndex.ToString();
        }

        /// <summary>
        /// 写非法语言 id 报告(l10n.missingIdsReport,默认 Output/missing_language_ids.csv,相对 CWD)并出汇总告警。
        /// 零缺失仍写仅表头的空报告(显式全绿);选项显式置空 = 关闭;写失败仅告警不阻断导出。
        /// 汇总 tag 用 [lan-index][missing-id-summary] 与逐格 [lan-index][missing-id] 区分,
        /// 保证按 tag grep 的告警计数与报告行数可精确对账。
        /// </summary>
        private static void WriteMissingIdsReport(List<MissingTextIdEntry> entries)
        {
            string path = EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily,
                BuiltinOptionNames.L10NMissingIdsReport, true, "Output/missing_language_ids.csv");
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }
            try
            {
                MissingTextIdReport.Write(path, entries);
            }
            catch (Exception e)
            {
                s_logger.Warn(e, "[lan-index][missing-id-report] 写语言 id 缺失报告失败(不阻断导出): {Path}", path);
            }
            if (entries.Count == 0)
            {
                s_logger.Info("[lan-index][missing-id-report] 无缺失/非法语言 id,已写仅表头报告: {Path}", path);
                return;
            }
            var uniqueValues = new SortedSet<string>(entries.Select(e => e.Value), StringComparer.Ordinal);
            s_logger.Warn("[lan-index][missing-id-summary] 共 {Cells} 个 text 格子引用了 {Count} 个缺失/非法语言 id(已全部导出为 -1;逐格明细见 {Path}): {Ids}",
                entries.Count, uniqueValues.Count, path, string.Join(", ", uniqueValues));
        }

        public static List<DefTable> MatchTables(GenerationContext ctx, L10NSpace space)
        {
            return ctx.Tables.Where(t => space.Tables.Contains(t.Name) || space.Tables.Contains(t.FullName)).ToList();
        }
    }
}
