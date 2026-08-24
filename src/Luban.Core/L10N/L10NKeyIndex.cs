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

namespace Luban.L10N;

/// <summary>
/// v2 显式 int 语言 id 注册表：
/// - <see cref="LiveIds"/> = 语言表 id 列的活 id 集，是 text 单元格静态校验(id∈集)的唯一依据。
/// 无墓碑：增量 diff 以冻结的基线 sidecar 快照为基准（基准后新增又删掉的 id 无需删除补丁，
/// 客户端从 0 重放 = 基线 + 最新补丁，本来就没有它）。id 即键，无位置下标语义。
/// </summary>
public class L10NKeyIndex
{
    public HashSet<int> LiveIds { get; }

    public int Count => LiveIds.Count;

    public L10NKeyIndex(HashSet<int> liveIds)
    {
        LiveIds = liveIds;
    }

    /// <summary>id 是否为活 id（语言表内存在）；未注册返回 false。</summary>
    public bool Contains(int id)
    {
        return LiveIds.Contains(id);
    }

    /// <summary>
    /// v2：id 即下标。字符串按 int 解析，活 id 返回其自身；非 int（旧 string key）或未注册返回 false。
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
