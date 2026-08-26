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
using System.Text;

namespace Luban.Serialization
{
    /// <summary>
    /// 二进制字符串表构建器：收集去重字符串（首见顺序分配 index），写出统一布局
    /// [WriteSize(count)] [WriteSize(len)×count] [UTF-8 blob 按序拼接]。
    /// 供 bin 全量表 / DLP1 / l10n 字典 / LLP2 共用（spec 2026-08-25）。
    /// </summary>
    public class StringTableBuilder
    {
        private readonly List<string> _strings = new List<string>();
        private readonly Dictionary<string, int> _indexMap = new Dictionary<string, int>();

        public int Count => _strings.Count;

        /// <summary>获取字符串 index（null 归一为空串），首次出现按首见顺序分配。</summary>
        public int GetOrAddIndex(string s)
        {
            if (s == null)
            {
                s = string.Empty;
            }
            if (_indexMap.TryGetValue(s, out int idx))
            {
                return idx;
            }
            idx = _strings.Count;
            _strings.Add(s);
            _indexMap.Add(s, idx);
            return idx;
        }

        /// <summary>写出字符串表到 buf。</summary>
        public void Write(ByteBuf buf)
        {
            int n = _strings.Count;
            var lengths = new int[n];
            for (int i = 0; i < n; i++)
            {
                lengths[i] = Encoding.UTF8.GetByteCount(_strings[i]);
            }
            buf.WriteSize(n);
            foreach (var len in lengths)
            {
                buf.WriteSize(len);
            }
            for (int i = 0; i < n; i++)
            {
                if (lengths[i] == 0)
                {
                    continue;
                }
                byte[] bytes = Encoding.UTF8.GetBytes(_strings[i]);
                buf.WriteBytesWithoutSize(bytes, 0, bytes.Length);
            }
        }
    }
}
