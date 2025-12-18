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

namespace Luban;

public static class L10NOptionUtil
{
    public static IReadOnlyList<string> GetLanguages()
    {
        string langs = EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily, "languages", false, "");
        if (string.IsNullOrWhiteSpace(langs))
        {
            return Array.Empty<string>();
        }

        return langs
            .Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Trim())
            .Where(s => !string.IsNullOrEmpty(s))
            .Distinct(StringComparer.Ordinal)
            .ToList();
    }

    public static string GetKeyFieldName()
    {
        // 与各 L10N 数据导出器保持一致的默认 key 字段解析
        return EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily,
            BuiltinOptionNames.L10NTextFileKeyFieldName, false, "id");
    }

    public static string GetKeyFieldDesc()
    {
        // 与各 L10N 数据导出器保持一致的默认 key 字段解析
        return EnvManager.Current.GetOptionOrDefault(BuiltinOptionNames.L10NFamily,
            BuiltinOptionNames.L10NTextFileKeyFieldDesc, false, "desc");
    }
}

