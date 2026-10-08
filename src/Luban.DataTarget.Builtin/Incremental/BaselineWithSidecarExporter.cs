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
using System.Text;
using Luban.Checksum;
using Luban.DataExporter.Builtin.Binary;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Incremental;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 基准导出 + 写基准 sidecar（普通表）。
/// 包装 TagSplitDataExporter：先正常 tag-split 产 .bytes，再用 BinaryDataVisitor 行字节
/// 算 per-row MD5（按目标 group 过滤字段），写 _baseline.{target}.sidecar.json。
/// sidecar 是工具内部产物，不 ship，供增量导出器做结构 gate + 行级 diff。
/// </summary>
[DataExporter("baseline-with-sidecar")]
public class BaselineWithSidecarExporter : TagSplitDataExporter
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 正常 tag-split 导出（产 .bytes；TableFilter != null 时排除被组合导出器接管的表）
        base.Handle(ctx, dataTarget, manifest);

        // 2. 写 sidecar（失败不阻断基准导出）
        try
        {
            WriteSidecar(ctx);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[baseline-with-sidecar] write sidecar failed: {e}");
        }
    }

    private void WriteSidecar(GenerationContext ctx)
    {
        // 从 -x incremental.sidecarPath=... 读 sidecar 路径；未配置则跳过
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var sidecar = new BaselineSidecar { Target = ctx.Target.Name, Version = SidecarFormat.CurrentVersion };
        foreach (var table in ctx.Tables)
        {
            if (table.Name == ChecksumTableBuilder.ChecksumTableName)
            {
                continue; // 虚拟 checksum 表不进 sidecar（数据 MD5 每次都会变，无增量意义）
            }
            if (TableFilter != null && TableFilter(table))
            {
                continue; // 组合导出时排除的表（space 语言表）不进普通表 sidecar，由各 space 自己的 sidecar 记录
            }

            var records = ctx.GetTableExportDataList(table);
            if (records == null || records.Count == 0)
            {
                continue;
            }

            var identity = GetIdentityIndex(table);
            ValidateIdentityFields(table, identity);
            // 行 diff 模式默认记身份索引名；身份键在数据上重复（字典覆盖，键数 < 行数）时降级整表替换
            string identityName = table.IsSingletonTable ? SidecarFormat.SingletonIdentityMarker : identity?.IndexName ?? "";
            var rowHashes = BuildRowHashes(table, records, identity);
            if (!table.IsSingletonTable && identity != null && rowHashes.Count != records.Count)
            {
                identityName = "";
                rowHashes = BuildRowHashes(table, records, null);
            }

            var entry = new TableSidecarEntry
            {
                SignatureId = table.SignatureId,
                Mode = table.Mode.ToString(),
                PrimaryKeyIndex = table.Index ?? "",
                IdentityIndex = identityName,
                RowCount = records.Count,
                ContentHash = table.ContentHash,
                Stamp = table.Stamp,
                RowHashes = rowHashes,
            };
            sidecar.Tables[table.FullName] = entry;
        }
        BaselineSidecarIO.Save(path, sidecar);
    }

    /// <summary>
    /// per-row MD5（BinaryDataVisitor.Ins 内联字符串，与增量 diff 同口径——索引模式会在字符串表 index 漂移时产生 diff 噪声）。
    /// identity != null 时键为身份索引全键；null 时键为记录序号（整表替换模式的变更检测口径）。
    /// </summary>
    private static Dictionary<string, string> BuildRowHashes(DefTable table, List<Record> records, IndexInfo identity)
    {
        var rowHashes = new Dictionary<string, string>(records.Count);
        foreach (var rec in records)
        {
            var buf = new ByteBuf();
            rec.Data.Apply(BinaryDataVisitor.Ins, buf);
            rowHashes[ExtractKey(table, rec, identity)] = FileUtil.CalcMD5(buf.CopyData());
        }
        return rowHashes;
    }

    /// <summary>
    /// 表的身份索引：行级 diff 与客户端 MergeApply 共同的行定位键。
    /// 规则与 cs 模板 first_single_index 一致：首个数据上非多值（键无重复）的索引组；单例表返回 null。
    /// 数据唯一性在两次导出间漂移会使两侧选择分叉，由增量导出的 IdentityIndex gate 拦截（需重跑基准同步客户端代码）。
    /// </summary>
    internal static IndexInfo GetIdentityIndex(DefTable table)
    {
        if (table.IsSingletonTable)
        {
            return null;
        }
        foreach (var idx in table.IndexList)
        {
            if (!idx.IsMultiValue)
            {
                return idx;
            }
        }
        return null;
    }

    /// <summary>sidecar IdentityIndex 字段值：单例表 "$one"，否则身份索引名（无则 "" = 整表替换模式）。</summary>
    internal static string GetIdentityIndexName(DefTable table)
    {
        return table.IsSingletonTable ? SidecarFormat.SingletonIdentityMarker : GetIdentityIndex(table)?.IndexName ?? "";
    }

    /// <summary>
    /// 身份索引字段类型约束：禁止 float/double（ToString 科学计数法可能含键分隔符 '+'，客户端无法还原行键）。
    /// </summary>
    internal static void ValidateIdentityFields(DefTable table, IndexInfo identity)
    {
        if (identity == null)
        {
            return;
        }
        foreach (var field in identity.IndexFields)
        {
            if (field.CType is TFloat or TDouble)
            {
                throw new InvalidOperationException($"[incremental] 表 '{table.FullName}' 身份索引 '{identity.IndexName}' 的字段 '{field.Name}' 为 float/double，不能作为行级 diff 身份键（键分隔符 '+' 与科学计数法冲突）；请改用整型/字符串/枚举字段或走整表替换");
            }
        }
    }

    /// <summary>
    /// 行键：identity != null 时为身份索引组全键（各字段 ToString 以 '+' 拼接，如 "101+3"，
    /// 与客户端 MergeApply delete 解析的切分口径一致）；null 时退化为记录序号（仅供整表替换模式的变更检测）。
    /// string 字段值含分隔符时报错，保证客户端按 '+' 切分可还原。
    /// </summary>
    internal static string ExtractKey(DefTable table, Record rec, IndexInfo identity)
    {
        if (identity == null)
        {
            return rec.AutoIndex.ToString();
        }
        var sb = new StringBuilder();
        for (int i = 0; i < identity.IndexFieldIdIndexes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(PatchFormat.KeySeparator);
            }
            var part = rec.Data.Fields[identity.IndexFieldIdIndexes[i]].ToString();
            if (part.Contains(PatchFormat.KeySeparator))
            {
                throw new InvalidOperationException($"[incremental] 表 '{table.FullName}' 身份索引 '{identity.IndexName}' 的字段值 '{part}' 含分隔符 '{PatchFormat.KeySeparator}'，无法生成客户端可还原的行键（string 键禁止包含 '+'）");
            }
            sb.Append(part);
        }
        return sb.ToString();
    }
}