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
using System.Linq;
using Luban.L10N;
using Xunit;

namespace Luban.Tests
{
    // v2:KeyIndexAllocator(自动分配下标)已退役;本测试覆盖其替代者 L10NKeyIndexBuilder 的
    // 纯函数接缝:活 id 集(注册表=语言表 id 列,无墓碑)、重复 id 硬错误、段表计算。
    public class L10NKeyIndexBuilderTests
    {
        [Fact]
        public void 重复id_硬错误()
        {
            var rows = new (int Id, string Origin)[]
            {
                (10001, "LanguageText[2]"),
                (10002, "LanguageText[3]"),
                (10001, "LanguageText[7]"),
            };
            var ex = Assert.Throws<TextKeyIndexException>(
                () => L10NKeyIndexBuilder.MergeRowIds(rows, "main"));
            Assert.Contains("10001", ex.Message);
        }

        [Fact]
        public void 注册表_等于当前活id集_无墓碑()
        {
            var currentIds = new HashSet<int> { 10002, 10001 };
            var registry = new L10NKeyIndex(currentIds);

            Assert.Equal(new[] { 10001, 10002 }, registry.LiveIds.OrderBy(i => i));
            Assert.Equal(2, registry.Count);
            Assert.True(registry.Contains(10001));
            Assert.False(registry.Contains(10099)); // 未注册 id 不是活 id,不能通过 text 单元格静态校验
        }

        [Fact]
        public void TryGetIndex_id即键()
        {
            var registry = new L10NKeyIndex(new HashSet<int> { 10034 });
            Assert.True(registry.TryGetIndex("10034", out var idx));
            Assert.Equal(10034, idx); // v2:id 即下标,不再是注册表位置
            Assert.False(registry.TryGetIndex("10099", out _)); // 未注册
            Assert.False(registry.TryGetIndex("old_key", out _)); // 非 int(旧 string key)
        }

        [Fact]
        public void GetSegment_按spec段表万级分段()
        {
            Assert.Equal(10000, L10NKeyIndexBuilder.GetSegment(10000));
            Assert.Equal(10000, L10NKeyIndexBuilder.GetSegment(10034));
            Assert.Equal(10000, L10NKeyIndexBuilder.GetSegment(19999));
            Assert.Equal(20000, L10NKeyIndexBuilder.GetSegment(20000));
            Assert.Equal(80000, L10NKeyIndexBuilder.GetSegment(89999));
            Assert.Equal(90000, L10NKeyIndexBuilder.GetSegment(90000)); // 预留段(段满顺延)
            Assert.Equal(100000, L10NKeyIndexBuilder.GetSegment(100000)); // 十万级扩展区/AOT
            Assert.Equal(100000, L10NKeyIndexBuilder.GetSegment(199999));
            Assert.Equal(0, L10NKeyIndexBuilder.GetSegment(9999)); // 低于最小段首:不属于任何段
        }

        [Theory]
        [InlineData(new[] { 10001, 10002, 19999 }, 0)]      // 同段 → 无跨段
        [InlineData(new[] { 10001, 20005 }, 1)]             // 跨段 → 20005 告警
        [InlineData(new[] { 5000, 10001 }, 1)]              // 段外 id(<10000)→ 5000 告警
        [InlineData(new[] { 100001, 150000 }, 0)]           // 扩展区整区同段
        [InlineData(new[] { 80000, 90001 }, 1)]             // 段满顺延 90000 段 → 告警(记录)
        [InlineData(new[] { 100, 5000, 9999 }, 3)]          // 全文件无合法段 id → 全部按段外告警
        [InlineData(new[] { 5000, 10001, 20005 }, 2)]       // 段外 id 与他段 id 同时告警
        public void FindCrossSegmentIds_跨段id集合(int[] ids, int expectedCount)
        {
            Assert.Equal(expectedCount, L10NKeyIndexBuilder.FindCrossSegmentIds(ids).Count);
        }

        [Fact]
        public void FindCrossSegmentIds_全文件无合法段id_全部按段外告警()
        {
            // 锚段为无段(所有 id < 10000,GetAnchorSegment 返回 NoSegment):文件内每个 id 都是跨段,
            // 与 GetAnchorSegment 契约"全文件无合法段 id 时返回无段(全部按段外告警)"一致。
            var foreign = L10NKeyIndexBuilder.FindCrossSegmentIds(new[] { 100, 5000, 9999 });
            Assert.Equal(new[] { 100, 5000, 9999 }, foreign);
        }

        [Fact]
        public void FindCrossSegmentIds_段外id无论锚段一律告警()
        {
            // 锚段合法时,自身段为无段(<10000)的 id 仍告警(与其余段的跨段 id 同等对待)
            Assert.Equal(new[] { 5000 }, L10NKeyIndexBuilder.FindCrossSegmentIds(new[] { 5000, 10001 }));
            Assert.Equal(new[] { 5000, 20005 }, L10NKeyIndexBuilder.FindCrossSegmentIds(new[] { 5000, 10001, 20005 }));
        }
    }
}
