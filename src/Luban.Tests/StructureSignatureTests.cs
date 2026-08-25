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
using Luban.Types;
using Luban.TypeVisitors;
using Xunit;

namespace Luban.Tests
{
    public class StructureSignatureTests
    {
        private static string Describe(TType type)
        {
            var sb = new StringBuilder();
            StructureSignature.AppendType(sb, type, null);
            return sb.ToString();
        }

        [Fact]
        public void 同字段类型_有无text标签_签名不同()
        {
            // string -> text 属于"仅加 tag 的类型迁移"，结构签名必须感知，
            // 否则 SignatureId 不变、增量基准失效（spec §4.7 / 风险3）。
            var plain = Describe(TString.Create(false, null));
            var tagged = Describe(TString.Create(false, new Dictionary<string, string> { { "text", "1" } }));
            Assert.NotEqual(plain, tagged);
        }

        [Fact]
        public void 相同tags不同插入顺序_签名相同()
        {
            // tags 序列化必须按 key 稳定排序，保证同一份表每次计算 SignatureId 一致。
            var first = Describe(TString.Create(false, new Dictionary<string, string> { { "a", "1" }, { "b", "2" } }));
            var second = Describe(TString.Create(false, new Dictionary<string, string> { { "b", "2" }, { "a", "1" } }));
            Assert.Equal(first, second);
        }
    }
}
