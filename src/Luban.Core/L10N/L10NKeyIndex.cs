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
using System.Globalization;
using System.Linq;
using Luban.Incremental;

namespace Luban.L10N;

/// <summary>
/// v2 显式 int 语言 id 注册表:
/// - <see cref="LiveIds"/> = 语言表 id 列的活 id 集,是 text 单元格静态校验(id∈集)的唯一依据;
/// - <see cref="Entries"/> = 活 id ∪ sidecar 墓碑差集(表内 id 优先于墓碑,即复活),供增量 sidecar 回写。
/// v2 中 id 即键:KeyEntry.Id 即语言表显式 id,条目位置无下标语义。
/// </summary>
public class L10NKeyIndex
{
    public HashSet<int> LiveIds { get; }

    public List<KeyEntry> Entries { get; }

    public int Count => Entries.Count;

    public L10NKeyIndex(HashSet<int> liveIds, IEnumerable<int> tombstoneIds)
    {
        LiveIds = liveIds;
        var tombstones = new HashSet<int>(tombstoneIds);
        tombstones.ExceptWith(liveIds); // 表内 id 复活优先:同 id 不再视为墓碑
        Entries = new List<KeyEntry>(liveIds.Count + tombstones.Count);
        foreach (var id in liveIds.Concat(tombstones).OrderBy(i => i))
        {
            Entries.Add(new KeyEntry { Id = id, Deleted = !liveIds.Contains(id) });
        }
    }

    /// <summary>id 是否为活 id(语言表内存在);墓碑/未注册返回 false。</summary>
    public bool Contains(int id)
    {
        return LiveIds.Contains(id);
    }

    /// <summary>
    /// v2:id 即下标。字符串按 int 解析,活 id 返回其自身;墓碑、非 int(旧 string key)或未注册返回 false。
    /// </summary>
    public bool TryGetIndex(string key, out int index)
    {
        if (int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && LiveIds.Contains(id))
        {
            index = id;
            return true;
        }
        index = -1;
        return false;
    }
}
