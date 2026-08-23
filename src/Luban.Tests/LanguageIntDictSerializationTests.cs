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
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using Luban.Incremental;
using Luban.Serialization;
using Luban.Types;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// v2 int 键紧凑字典序列化（LanguageArraySerializationTests 的替代——数组序列化已随
    /// spec 2026-08-22 退役删除）：[WriteSize: count] [WriteInt(id) WriteString(value)]*。
    /// 与 server space 现行 bin 及 main/aot space 基准 bin（omnibus 合并导出）逐字节同布局，
    /// 也是 ComputePerLanguageFileMd5 的指纹口径。
    /// </summary>
    public class LanguageIntDictSerializationTests
    {
        [Fact]
        public void int字典布局_id与值()
        {
            // Dictionary<object,string> 迭代序不确定,但 (id,value) 成对绑定,布局断言与序无关
            var map = new Dictionary<object, string> { [10001] = "VA", [20002] = "VB" };
            byte[] bytes = L10NChecksumUtil.SerializeLanguageBytes(map, TInt.Create(false, null));

            var buf = new ByteBuf(bytes);
            Assert.Equal(2, buf.ReadSize());
            var got = new Dictionary<int, string>();
            for (int i = 0; i < 2; i++)
            {
                int id = buf.ReadInt();
                got[id] = buf.ReadString();
            }
            Assert.Equal("VA", got[10001]);
            Assert.Equal("VB", got[20002]);
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void int字典布局_null值为空串()
        {
            var map = new Dictionary<object, string> { [10001] = null };
            byte[] bytes = L10NChecksumUtil.SerializeLanguageBytes(map, TInt.Create(false, null));

            var buf = new ByteBuf(bytes);
            Assert.Equal(1, buf.ReadSize());
            Assert.Equal(10001, buf.ReadInt());
            Assert.Equal("", buf.ReadString());
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void int字典布局_空表_仅写count零()
        {
            byte[] bytes = L10NChecksumUtil.SerializeLanguageBytes(
                new Dictionary<object, string>(), TInt.Create(false, null));

            var buf = new ByteBuf(bytes);
            Assert.Equal(0, buf.ReadSize());
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void int字典布局_多字节值按UTF8写出()
        {
            var map = new Dictionary<object, string> { [10034] = "中文·值" };
            byte[] bytes = L10NChecksumUtil.SerializeLanguageBytes(map, TInt.Create(false, null));

            var buf = new ByteBuf(bytes);
            Assert.Equal(1, buf.ReadSize());
            Assert.Equal(10034, buf.ReadInt());
            Assert.Equal("中文·值", buf.ReadString());
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void ToIntId_int家族装箱值转换_string报错()
        {
            Assert.Equal(10001, L10NChecksumUtil.ToIntId(10001));
            Assert.Equal(10002, L10NChecksumUtil.ToIntId((long)10002));
            Assert.Equal(10003, L10NChecksumUtil.ToIntId((short)10003));
            Assert.Equal(4, L10NChecksumUtil.ToIntId((byte)4));
            Assert.Throws<NotSupportedException>(() => L10NChecksumUtil.ToIntId("btn_ok"));
        }
    }
}
