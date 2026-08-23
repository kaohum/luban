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
using Luban.Datas;
using Luban.L10N;
using Luban.Types;
using Xunit;

namespace Luban.Tests
{
    // v2(spec 2026-08-22):text 单元格内容为 int 字面量字符串,不再查 string 注册表。
    // 三态:合法 id→DInt(id);parse 失败/id∉语言表 id 集→DInt(-1)+[lan-index][missing-id] 收集(R12);空→DInt(-1)。
    public class TextKeyIndexTransformerTests
    {
        private static readonly HashSet<int> LiveIds = new() { 10001, 10034, 20015 };

        private static TString TextType()
        {
            return TString.Create(false, new Dictionary<string, string> { { "text", "1" } });
        }

        private static DInt ApplyAsInt(string cell, SortedSet<string> missing = null)
        {
            missing ??= new SortedSet<string>();
            var trans = new TextKeyIndexTransformer(LiveIds, "TbItem", "nameId", "id=1001", missing);
            var result = trans.Apply(DString.ValueOf(TextType(), cell), TextType());
            Assert.IsType<DInt>(result);
            return (DInt)result;
        }

        [Fact]
        public void 合法id_转DInt原值()
        {
            var missing = new SortedSet<string>();
            Assert.Equal(10034, ApplyAsInt("10034", missing).Value);
            Assert.Equal(10001, ApplyAsInt("10001", missing).Value);
            Assert.Empty(missing);
        }

        [Fact]
        public void id不在语言表_导出负一并收集()
        {
            var missing = new SortedSet<string>();
            Assert.Equal(-1, ApplyAsInt("19999", missing).Value);
            Assert.Contains("19999", missing);
        }

        [Fact]
        public void 非int字面量_导出负一并收集()
        {
            // 旧 string key 形态(如 "ok_button")在 v2 是非法单元格:parse 失败按缺失处理
            var missing = new SortedSet<string>();
            Assert.Equal(-1, ApplyAsInt("ok_button", missing).Value);
            Assert.Contains("ok_button", missing);
        }

        [Fact]
        public void 空值_转负一哨兵且不收集()
        {
            var missing = new SortedSet<string>();
            Assert.Equal(-1, ApplyAsInt("", missing).Value);
            Assert.Empty(missing);
        }

        [Fact]
        public void 无text标签_原样返回()
        {
            var type = TString.Create(false, null);
            var data = DString.ValueOf(type, "10034");
            var trans = new TextKeyIndexTransformer(LiveIds, "Tb", "f1", "row1", new SortedSet<string>());
            Assert.Same(data, trans.Apply(data, type));
        }
    }
}
