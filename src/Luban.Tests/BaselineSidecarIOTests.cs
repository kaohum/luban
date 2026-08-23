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

using System.IO;
using Luban.Incremental;
using Xunit;

namespace Luban.Tests
{
    public class BaselineSidecarIOTests
    {
        [Fact]
        public void KeyEntries_序列化往返_保序保墓碑()
        {
            string path = Path.Combine(Path.GetTempPath(), $"l10n_test_{System.Guid.NewGuid():N}.json");
            try
            {
                var s = new L10NSidecar { SignatureId = "sig1" };
                s.KeyEntries.Add(new KeyEntry { Id = 10001 });
                s.KeyEntries.Add(new KeyEntry { Id = 10002, Deleted = true });
                BaselineSidecarIO.SaveL10N(path, s);

                // 墓碑判定依赖默认值,必须确保序列化选项不会忽略 Deleted=false(如 WhenWritingDefault)
                string raw = File.ReadAllText(path);
                Assert.Contains("\"Deleted\"", raw);

                var loaded = BaselineSidecarIO.LoadL10N(path);
                Assert.Equal(2, loaded.KeyEntries.Count);
                Assert.Equal(10001, loaded.KeyEntries[0].Id);
                Assert.False(loaded.KeyEntries[0].Deleted);
                Assert.Equal(10002, loaded.KeyEntries[1].Id);
                Assert.True(loaded.KeyEntries[1].Deleted);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void 旧格式sidecar_无KeyEntries_读入为空且不报错()
        {
            string path = Path.Combine(Path.GetTempPath(), $"l10n_old_{System.Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path, @"{""SignatureId"":""s"",""Keys"":[],""Languages"":{}}");
                var loaded = BaselineSidecarIO.LoadL10N(path);
                Assert.NotNull(loaded);
                Assert.Empty(loaded.KeyEntries);
            }
            finally
            {
                File.Delete(path);
            }
        }

        [Fact]
        public void v1字符串key的sidecar_读入视为空_等价重基准()
        {
            // v1 格式(string Keys/KeyEntry.Key)在 v2 int 模型下反序列化失败 → 整体视为空 sidecar
            // (spec v2 §5:迁移即删旧 sidecar 全量重基准,无兼容负担)
            string path = Path.Combine(Path.GetTempPath(), $"l10n_v1_{System.Guid.NewGuid():N}.json");
            try
            {
                File.WriteAllText(path,
                    @"{""SignatureId"":""sig1"",""Keys"":[""btn_ok""],""KeyEntries"":[{""Key"":""btn_ok"",""Deleted"":false}],""Languages"":{}}");
                var loaded = BaselineSidecarIO.LoadL10N(path);
                Assert.NotNull(loaded);
                Assert.Equal("", loaded.SignatureId); // 整体视为空(结构 gate 将要求重基准)
                Assert.Empty(loaded.Keys);
                Assert.Empty(loaded.KeyEntries);
                Assert.Empty(loaded.Languages);
            }
            finally
            {
                File.Delete(path);
            }
        }
    }
}
