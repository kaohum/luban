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

using System.Text;
using Luban.DataExporter.Builtin.Incremental;
using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// LLP2（L10N index 模式 delta patch）字节布局：magic + string sigId +
    /// varint upsertCount + (int id, string val)* + varint delCount + int id*。
    /// v2（spec 2026-08-22）：字段 = 显式 int 语言 id（不再是数组注册表位置），布局不变。
    /// </summary>
    public class LLP2FormatTests
    {
        [Fact]
        public void 布局_id与值()
        {
            var buf = new ByteBuf();
            PatchFormat.WriteMagic(buf, PatchFormat.MagicL10N2);
            buf.WriteString("SIG");
            buf.WriteSize(1);
            buf.WriteInt(7);
            buf.WriteString("V7");
            buf.WriteSize(1);
            buf.WriteInt(3);

            var r = new ByteBuf(buf.CopyData());
            // ByteBuf 无 ReadBytes(int) 重载，逐字节读 4 字节 magic
            var magic = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                magic[i] = r.ReadByte();
            }
            Assert.Equal("LLP2", Encoding.UTF8.GetString(magic));
            Assert.Equal("SIG", r.ReadString());
            Assert.Equal(1, r.ReadSize());
            Assert.Equal(7, r.ReadInt());
            Assert.Equal("V7", r.ReadString());
            Assert.Equal(1, r.ReadSize());
            Assert.Equal(3, r.ReadInt());
            Assert.Equal(0, r.Remaining);
        }
    }
}
