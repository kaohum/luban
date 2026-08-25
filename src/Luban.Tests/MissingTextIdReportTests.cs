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

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Luban.L10N;
using Xunit;

namespace Luban.Tests
{
    // l10n.missingIdsReport 报告(l10n-report):逐格明细 CSV,幂等门禁要求同输入恒定字节。
    // 编码契约:UTF-8 BOM + CRLF(策划 Excel 直接打开);零缺失 -> 仅表头空报告(显式全绿)。
    public class MissingTextIdReportTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "luban-missing-id-report-" + Guid.NewGuid().ToString("N"));

        private string WriteTemp(params MissingTextIdEntry[] entries)
        {
            Directory.CreateDirectory(_dir);
            var path = Path.Combine(_dir, "missing_language_ids.csv");
            MissingTextIdReport.Write(path, entries);
            return path;
        }

        private static byte[] ReadAll(string path)
        {
            return File.ReadAllBytes(path);
        }

        [Fact]
        public void 零缺失_仅表头_BOM加CRLF()
        {
            var path = WriteTemp();
            var bytes = ReadAll(path);
            // UTF-8 BOM
            Assert.Equal(new byte[] { 0xEF, 0xBB, 0xBF }, bytes.Take(3).ToArray());
            var expected = "表,行标识,列,填写值,原因\r\n";
            var body = new byte[bytes.Length - 3];
            Array.Copy(bytes, 3, body, 0, body.Length);
            Assert.Equal(expected, System.Text.Encoding.UTF8.GetString(body));
        }

        [Fact]
        public void 逐格明细_排序_原因拆分_字节精确()
        {
            var path = WriteTemp(
                new MissingTextIdEntry("table.b", "2", "nameId", "ok_button", MissingTextIdReason.NotANumber),
                new MissingTextIdEntry("table.a", "10", "descId", "30001", MissingTextIdReason.IdNotExists),
                new MissingTextIdEntry("table.a", "2", "nameId", "-1", MissingTextIdReason.IdNotExists),
                new MissingTextIdEntry("table.a", "2", "descId", "ok_button", MissingTextIdReason.NotANumber));
            var text = System.Text.Encoding.UTF8.GetString(ReadAll(path)).Substring(1); // 去 BOM
            // 行标识数字序：2 < 10（非字典序）；表 ordinal：table.a < table.b；列 ordinal：descId < nameId
            var expected = "表,行标识,列,填写值,原因\r\n"
                + "table.a,2,descId,ok_button,非数字\r\n"
                + "table.a,2,nameId,-1,id不存在\r\n"
                + "table.a,10,descId,30001,id不存在\r\n"
                + "table.b,2,nameId,ok_button,非数字\r\n";
            Assert.Equal(expected, text);
        }

        [Fact]
        public void 行标识非数字行_排在数字行之后()
        {
            // 组合键（'+' 连接）不可整解析 -> ordinal 段，排在所有数字行标识之后
            var path = WriteTemp(
                new MissingTextIdEntry("t", "3+5", "f", "x", MissingTextIdReason.NotANumber),
                new MissingTextIdEntry("t", "20", "f", "y", MissingTextIdReason.NotANumber),
                new MissingTextIdEntry("t", "100", "f", "z", MissingTextIdReason.NotANumber));
            var text = System.Text.Encoding.UTF8.GetString(ReadAll(path)).Substring(1);
            var expected = "表,行标识,列,填写值,原因\r\n"
                + "t,20,f,y,非数字\r\n"
                + "t,100,f,z,非数字\r\n"
                + "t,3+5,f,x,非数字\r\n";
            Assert.Equal(expected, text);
        }

        [Fact]
        public void 同键多格_稳定排序保持收集序()
        {
            // 容器内多个 text 元素共享 (表,行标识,列):OrderBy 稳定排序保持插入序 -> 字节确定
            var path = WriteTemp(
                new MissingTextIdEntry("t", "1", "items.nameId", "30001", MissingTextIdReason.IdNotExists),
                new MissingTextIdEntry("t", "1", "items.nameId", "ok_button", MissingTextIdReason.NotANumber));
            var text = System.Text.Encoding.UTF8.GetString(ReadAll(path)).Substring(1);
            var expected = "表,行标识,列,填写值,原因\r\n"
                + "t,1,items.nameId,30001,id不存在\r\n"
                + "t,1,items.nameId,ok_button,非数字\r\n";
            Assert.Equal(expected, text);
        }

        [Fact]
        public void 值含逗号引号换行_RFC4180转义()
        {
            var path = WriteTemp(
                new MissingTextIdEntry("t", "1", "f", "a,b", MissingTextIdReason.NotANumber),
                new MissingTextIdEntry("t", "2", "f", "say \"hi\"", MissingTextIdReason.NotANumber),
                new MissingTextIdEntry("t", "3", "f", "line1\r\nline2", MissingTextIdReason.NotANumber));
            var text = System.Text.Encoding.UTF8.GetString(ReadAll(path)).Substring(1);
            // 字段内 CR/LF 原样保留(转义为引号包裹),文件行分隔符仍恒为 CRLF
            var expected = "表,行标识,列,填写值,原因\r\n"
                + "t,1,f,\"a,b\",非数字\r\n"
                + "t,2,f,\"say \"\"hi\"\"\",非数字\r\n"
                + "t,3,f,\"line1\r\nline2\",非数字\r\n";
            Assert.Equal(expected, text);
        }

        [Fact]
        public void 同输入两次写出_字节一致()
        {
            var entries = new[]
            {
                new MissingTextIdEntry("b", "1", "f", "x", MissingTextIdReason.NotANumber),
                new MissingTextIdEntry("a", "9", "g", "30001", MissingTextIdReason.IdNotExists),
            };
            Directory.CreateDirectory(_dir);
            var p1 = Path.Combine(_dir, "r1.csv");
            var p2 = Path.Combine(_dir, "r2.csv");
            MissingTextIdReport.Write(p1, entries);
            MissingTextIdReport.Write(p2, entries);
            Assert.Equal(ReadAll(p1), ReadAll(p2));
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_dir, true);
            }
            catch (Exception)
            {
                // 临时目录清理失败不影响测试结果
            }
        }
    }
}
