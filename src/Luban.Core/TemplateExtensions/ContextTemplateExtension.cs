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

using Luban.CodeTarget;
using Luban.Defs;
using Scriban.Runtime;

namespace Luban.TemplateExtensions;

public class ContextTemplateExtension : ScriptObject
{

    /// <summary>
    /// 表是否被当前代码目标排除出表级代码生成（{target}.excludeTables）。
    /// tables_by_tag 等数据层表集合不会经过代码目标的表过滤，
    /// 模板以其生成表名引用（如 TableNamesWith 数组引用 ConfigNameType 成员）时必须用它排除，
    /// 否则会引用到不存在的表级符号导致生成代码编译失败。
    /// </summary>
    public static bool IsExcludedTable(DefTable table)
    {
        return GenerationContext.CurrentCodeTarget is CodeTargetBase codeTarget && codeTarget.IsTableExcluded(table);
    }

    public static bool HasTag(dynamic obj, string attrName)
    {
        return obj.HasTag(attrName);
    }

    public static string GetTag(dynamic obj, string attrName)
    {
        return obj.GetTag(attrName);
    }

    public static bool HasOption(string name)
    {
        return EnvManager.Current.HasOptionRaw(name);
    }

    public static string GetOption(string name)
    {
        return EnvManager.Current.GetOptionRaw(name);
    }

    public static string GetOptionOrDefault(string name, string defaultValue)
    {
        return EnvManager.Current.GetOptionOrDefaultRaw(name, defaultValue);
    }
}
