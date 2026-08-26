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

using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    public class StringTableBuilderTests
    {
        [Fact]
        public void 去重与首见顺序索引()
        {
            var b = new StringTableBuilder();
            Assert.Equal(0, b.GetOrAddIndex("a"));
            Assert.Equal(1, b.GetOrAddIndex("bb"));
            Assert.Equal(0, b.GetOrAddIndex("a")); // 去重
            Assert.Equal(2, b.GetOrAddIndex(""));  // 空串自然入表
            Assert.Equal(2, b.GetOrAddIndex(null)); // null → 空串
            Assert.Equal(3, b.Count);
        }

        [Fact]
        public void 写出布局_索引区加blob()
        {
            var b = new StringTableBuilder();
            b.GetOrAddIndex("a");
            b.GetOrAddIndex("bb");
            b.GetOrAddIndex("中文");
            var buf = new ByteBuf();
            b.Write(buf);
            // count
            Assert.Equal(3, buf.ReadSize());
            // 索引区 len（0x61='a', 0x62='b', 中文UTF8=6字节）
            Assert.Equal(1, buf.ReadSize());
            Assert.Equal(2, buf.ReadSize());
            Assert.Equal(6, buf.ReadSize());
            // blob：a bb 中文(UTF8)
            Assert.Equal((byte)'a', buf.ReadByte());
            Assert.Equal((byte)'b', buf.ReadByte());
            Assert.Equal((byte)'b', buf.ReadByte());
            byte[] zh = new byte[6];
            for (int i = 0; i < 6; i++) zh[i] = buf.ReadByte();
            Assert.Equal("中文", System.Text.Encoding.UTF8.GetString(zh));
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void 空表_仅count零()
        {
            var buf = new ByteBuf();
            new StringTableBuilder().Write(buf);
            Assert.Equal(0, buf.ReadSize());
            Assert.Equal(0, buf.Remaining);
        }
    }
}
