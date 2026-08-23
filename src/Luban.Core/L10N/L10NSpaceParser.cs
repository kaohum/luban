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

namespace Luban.L10N;

/// <summary>
/// 解析 l10n.spaces 及 l10n.{space}.* 分组选项。未配置 l10n.spaces 时返回空表(走旧单值选项路径)。
/// </summary>
public static class L10NSpaceParser
{
    public static List<L10NSpace> Parse(IReadOnlyDictionary<string, string> options)
    {
        var result = new List<L10NSpace>();
        if (!options.TryGetValue("l10n.spaces", out var spacesStr) || string.IsNullOrWhiteSpace(spacesStr))
        {
            return result;
        }
        foreach (var name in spacesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string P(string key, string def = "") =>
                options.TryGetValue($"l10n.{name}.{key}", out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

            var space = new L10NSpace
            {
                Name = name,
                Tables = P("tables").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                IndexMode = string.Equals(P("indexMode", "false"), "true", StringComparison.OrdinalIgnoreCase),
                Languages = P("languages").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                OutputFile = P("outputFile", "languageconfig"),
                OutputDir = P("outputDir", name),
                KeyFieldName = P("keyFieldName", "key"),
                KeyFieldDesc = P("keyFieldDesc", "zh_CN"),
                NameFieldName = P("nameFieldName", "name"),
                // 默认 true(向后兼容);仅显式 "false"(忽略大小写)关闭,其余值(含拼错)保持 true
                GenAccessors = !string.Equals(P("genAccessors", "true"), "false", StringComparison.OrdinalIgnoreCase),
                SidecarPath = P("sidecar"),
            };
            result.Add(space);
        }
        return result;
    }
}
