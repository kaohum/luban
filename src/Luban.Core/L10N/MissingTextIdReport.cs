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
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Luban.L10N;

/// <summary>text 单元格非法语言 id 的原因。</summary>
public enum MissingTextIdReason
{
    /// <summary>parse 失败（旧 string key、自由文本等非 int 字面量）。</summary>
    NotANumber,

    /// <summary>parse 成功但 id 不在语言表活 id 集（含 -1 字面量与已删除的陈旧 id）。</summary>
    IdNotExists,
}

/// <summary>
/// 一个非法 text 单元格的明细：表、行标识（主索引值，无索引退化为物理行号）、
/// 列（bean 内字段路径，'.' 连接）、填写值（单元格原文）、原因。
/// 空 text 单元格按设计合法，不产生条目。
/// </summary>
public class MissingTextIdEntry
{
    public string Table { get; }

    public string Row { get; }

    public string Field { get; }

    public string Value { get; }

    public MissingTextIdReason Reason { get; }

    public MissingTextIdEntry(string table, string row, string field, string value, MissingTextIdReason reason)
    {
        Table = table;
        Row = row;
        Field = field;
        Value = value;
        Reason = reason;
    }
}

/// <summary>
/// 非法语言 id CSV 报告（l10n.missingIdsReport，默认 Output/missing_language_ids.csv）。
/// 每次导出全量刷新：按（表,行标识,列）稳定排序 -> 同输入恒定字节（幂等门禁）；
/// UTF-8 BOM + CRLF（策划用 Excel 直接打开）；RFC 4180 转义（值可含逗号/引号/换行）；
/// 零缺失时仍写仅含表头的空报告（显式"全绿"）。
/// </summary>
public static class MissingTextIdReport
{
    public const string Header = "表,行标识,列,填写值,原因";

    public static string ReasonText(MissingTextIdReason reason)
    {
        return reason switch
        {
            MissingTextIdReason.NotANumber => "非数字",
            MissingTextIdReason.IdNotExists => "id不存在",
            _ => reason.ToString(),
        };
    }

    /// <summary>渲染报告全文（含表头；BOM 由写文件时编码注入）。</summary>
    public static string Render(IEnumerable<MissingTextIdEntry> entries)
    {
        // OrderBy 为稳定排序：同 (表,行标识,列) 的多条（容器内多个 text 元素）保持遍历序，字节确定。
        // 表/列用 ordinal；行标识用数字优先比较器（int 主键按数值序，人读友好且仍为全序）。
        var sorted = entries.OrderBy(e => e.Table, StringComparer.Ordinal)
            .ThenBy(e => e.Row, RowIdComparer.Instance)
            .ThenBy(e => e.Field, StringComparer.Ordinal);
        var sb = new StringBuilder();
        sb.Append(Header);
        foreach (var e in sorted)
        {
            sb.Append("\r\n");
            AppendField(sb, e.Table);
            sb.Append(',');
            AppendField(sb, e.Row);
            sb.Append(',');
            AppendField(sb, e.Field);
            sb.Append(',');
            AppendField(sb, e.Value);
            sb.Append(',');
            AppendField(sb, ReasonText(e.Reason));
        }
        sb.Append("\r\n");
        return sb.ToString();
    }

    /// <summary>写报告文件（建目录、UTF-8 BOM）。零条目 -> 仅表头一行。silent=true 时跳过写入（文件保持原状）。</summary>
    public static void Write(string path, IReadOnlyList<MissingTextIdEntry> entries, bool silent = false)
    {
        if (silent)
        {
            // 静默模式(l10n.silentMissingWarn):CSV 由客户端 omnibus 调用独占写入,服务器等重复调用不触碰
            return;
        }
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, Render(entries ?? Array.Empty<MissingTextIdEntry>()), new UTF8Encoding(true));
    }

    /// <summary>RFC 4180：含逗号/引号/CR/LF 的字段加引号包裹，内部引号翻倍。</summary>
    private static void AppendField(StringBuilder sb, string value)
    {
        value ??= "";
        if (value.IndexOfAny(new[] { ',', '"', '\r', '\n' }) >= 0)
        {
            sb.Append('"').Append(value.Replace("\"", "\"\"")).Append('"');
        }
        else
        {
            sb.Append(value);
        }
    }

    /// <summary>
    /// 行标识全序比较器（人读友好且确定性）：可解析为 long 的行标识（int 主键/AutoIndex 兜底）
    /// 按数值序排在前面（数值相同再按 ordinal 决胜，如 "10"/"010"）；不可解析的（组合键 "1+2" 等）
    /// 按 ordinal 排在所有数字行之后。类间次序固定，保证传递性成立（全序）。
    /// </summary>
    private sealed class RowIdComparer : IComparer<string>
    {
        public static readonly RowIdComparer Instance = new();

        public int Compare(string x, string y)
        {
            bool nx = long.TryParse(x, out var vx);
            bool ny = long.TryParse(y, out var vy);
            if (nx && ny)
            {
                return vx != vy ? vx.CompareTo(vy) : string.CompareOrdinal(x, y);
            }
            if (nx != ny)
            {
                return nx ? -1 : 1; // 数字行在前
            }
            return string.CompareOrdinal(x, y);
        }
    }
}
