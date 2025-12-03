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

﻿using Luban.Datas;

namespace Luban.Defs;

public class Record
{
    public const string DefaultTag = "base";

    public int AutoIndex { get; set; }

    public DBean Data { get; set; }

    public string Source { get; }

    public List<string> Tags { get; }

    public bool IsNotFiltered(List<string> includeTags, List<string> excludeTags)
    {
        // 注意：为保持历史行为，只有“有效业务 tag”才参与 include/exclude 过滤，
        // 仅带有默认 tag(_base_) 的记录在过滤时视为“无 tag”记录。
        bool hasEffectiveTags = Tags != null && Tags.Any(t => !string.Equals(t, DefaultTag, StringComparison.OrdinalIgnoreCase));

        if (!hasEffectiveTags)
        {
            // 兼容旧逻辑：无 tag 记录在未显式排除的情况下总是导出
            if (excludeTags != null && excludeTags.Contains(DefaultTag))
            {
                return false;
            }
            return true;
        }

        if (includeTags != null && includeTags.Count > 0)
        {
            return Tags.Any(includeTags.Contains);
        }

        if (excludeTags != null && excludeTags.Count > 0)
        {
            return !Tags.Any(excludeTags.Contains);
        }

        return true;
    }

    public Record(DBean data, string source, List<string> tags)
    {
        Data = data;
        Source = source;

        #region 如果没有Tag则给定一个默认Tag

        if (tags == null)
        {
            tags = new List<string>();
        }

        if (tags.Count == 0)
        {
            tags.Add(DefaultTag);
        }
        
        #endregion
        Tags = tags;
    }
}
