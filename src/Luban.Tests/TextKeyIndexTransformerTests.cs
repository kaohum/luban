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
    // 三态:合法 id->DInt(id);parse 失败/id∉语言表 id 集->DInt(-1)+[lan-index][missing-id]
    // 逐格收集(表,行标识,列,填写值,原因);空->DInt(-1) 不收集(按设计合法)。
    public class TextKeyIndexTransformerTests
    {
        private static readonly HashSet<int> LiveIds = new() { 10001, 10034, 20015 };

        private static TString TextType()
        {
            return TString.Create(false, new Dictionary<string, string> { { "text", "1" } });
        }

        private static DInt ApplyAsInt(string cell, List<MissingTextIdEntry> missing = null)
        {
            missing ??= new List<MissingTextIdEntry>();
            var trans = new TextKeyIndexTransformer(LiveIds, "TbItem", "nameId", "1001", missing);
            var result = trans.Apply(DString.ValueOf(TextType(), cell), TextType());
            Assert.IsType<DInt>(result);
            return (DInt)result;
        }

        [Fact]
        public void 合法id_转DInt原值()
        {
            var missing = new List<MissingTextIdEntry>();
            Assert.Equal(10034, ApplyAsInt("10034", missing).Value);
            Assert.Equal(10001, ApplyAsInt("10001", missing).Value);
            Assert.Empty(missing);
        }

        [Fact]
        public void id不在语言表_导出负一并按格收集_id不存在()
        {
            var missing = new List<MissingTextIdEntry>();
            Assert.Equal(-1, ApplyAsInt("19999", missing).Value);
            var entry = Assert.Single(missing);
            Assert.Equal("TbItem", entry.Table);
            Assert.Equal("1001", entry.Row);
            Assert.Equal("nameId", entry.Field);
            Assert.Equal("19999", entry.Value);
            Assert.Equal(MissingTextIdReason.IdNotExists, entry.Reason);
        }

        [Fact]
        public void 负一字面量_按id不存在收集()
        {
            var missing = new List<MissingTextIdEntry>();
            Assert.Equal(-1, ApplyAsInt("-1", missing).Value);
            Assert.Equal(MissingTextIdReason.IdNotExists, Assert.Single(missing).Reason);
        }

        [Fact]
        public void 非int字面量_导出负一并按格收集_非数字()
        {
            // 旧 string key 形态(如 "ok_button")在 v2 是非法单元格:parse 失败按非数字处理
            var missing = new List<MissingTextIdEntry>();
            Assert.Equal(-1, ApplyAsInt("ok_button", missing).Value);
            var entry = Assert.Single(missing);
            Assert.Equal("ok_button", entry.Value);
            Assert.Equal(MissingTextIdReason.NotANumber, entry.Reason);
        }

        [Fact]
        public void 空值_转负一哨兵且不收集()
        {
            var missing = new List<MissingTextIdEntry>();
            Assert.Equal(-1, ApplyAsInt("", missing).Value);
            Assert.Empty(missing);
        }

        [Fact]
        public void 无text标签_原样返回()
        {
            var type = TString.Create(false, null);
            var data = DString.ValueOf(type, "10034");
            var trans = new TextKeyIndexTransformer(LiveIds, "Tb", "f1", "row1", new List<MissingTextIdEntry>());
            Assert.Same(data, trans.Apply(data, type));
        }

        [Fact]
        public void 同值多格_逐格各收集一条()
        {
            var missing = new List<MissingTextIdEntry>();
            var trans = new TextKeyIndexTransformer(LiveIds, "Tb", "f1", "9", missing);
            trans.Apply(DString.ValueOf(TextType(), "19999"), TextType());
            trans.Apply(DString.ValueOf(TextType(), "19999"), TextType());
            Assert.Equal(2, missing.Count); // 报告行数 = 非法格子数(非唯一值数)
        }
    }
}
