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

using System.Collections.Generic;
using System.Linq;
using Luban.CodeTarget;
using Luban.CSharp.TemplateExtensions;
using Luban.Defs;
using NLog;
using Scriban;

namespace Luban.CSharp.CodeTarget;

[CodeTarget("cs-bin")]
public class CsharpBinCodeTarget : CsharpCodeTargetBase
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    protected override void OnCreateTemplateContext(TemplateContext ctx)
    {
        base.OnCreateTemplateContext(ctx);
        ctx.PushGlobal(new CsharpBinTemplateExtension());
    }

    /// <summary>
    /// cs-bin.excludeTables：被命中的表不生成表级代码（表类文件、Tables manager 条目、ConfigNameType 等表派生枚举项），
    /// 用于语言表这类由 cs-l10n-language 负责代码生成、cs-bin 表级类会与之撞名的场景。
    /// record bean 类不受影响（仍正常生成）；未配置该选项时零行为。
    /// </summary>
    private HashSet<string> _excludeTables;

    private bool _excludeTablesParsed;

    private HashSet<string> ExcludeTables
    {
        get
        {
            if (!_excludeTablesParsed)
            {
                _excludeTables = ParseExcludeTables(Name);
                _excludeTablesParsed = true;
            }
            return _excludeTables;
        }
    }

    public override bool IsTableExcluded(DefTable table)
    {
        return IsExcluded(table, ExcludeTables);
    }

    protected override List<DefTable> FilterTablesForCode(GenerationContext ctx)
    {
        var tables = base.FilterTablesForCode(ctx);
        var excludes = ExcludeTables;
        if (excludes == null)
        {
            return tables;
        }
        var excluded = tables.Where(t => IsExcluded(t, excludes)).Select(t => t.FullName).OrderBy(n => n).ToList();
        // 单行 INFO：bat 输出可见，确认排除生效（0 命中提示配置可能写错表名）
        s_logger.Info($"[{Name}] excludeTables 生效: {excluded.Count}/{tables.Count} 张表跳过表级代码生成: " +
            (excluded.Count > 0 ? string.Join(",", excluded) : "(无命中,请检查表名)"));
        return tables.Where(t => !IsExcluded(t, excludes)).ToList();
    }
}
