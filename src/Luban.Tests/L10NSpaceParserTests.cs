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
using Luban.L10N;
using Xunit;

namespace Luban.Tests
{
    public class L10NSpaceParserTests
    {
        [Fact]
        public void 解析多space_全部字段()
        {
            var opts = new Dictionary<string, string>
            {
                ["l10n.spaces"] = "main,aot,server",
                ["l10n.main.tables"] = "LanguageText",
                ["l10n.main.indexMode"] = "true",
                ["l10n.main.languages"] = "zh_CN,en_US",
                ["l10n.main.outputFile"] = "languageconfig",
                ["l10n.main.outputDir"] = "language",
                ["l10n.main.keyFieldName"] = "key",
                ["l10n.main.sidecar"] = @"Output\baseline\l10n.json",
                ["l10n.aot.tables"] = "LanguageAOTText",
                ["l10n.aot.indexMode"] = "true",
                ["l10n.aot.sidecar"] = @"Output\baseline\l10n.aot.json",
                ["l10n.server.tables"] = "LanguageServer",
                ["l10n.server.indexMode"] = "false",
            };
            var spaces = L10NSpaceParser.Parse(opts);
            Assert.Equal(3, spaces.Count);

            var main = spaces[0];
            Assert.Equal("main", main.Name);
            Assert.True(main.IndexMode);
            Assert.Equal(new[] { "zh_CN", "en_US" }, main.Languages);
            Assert.Equal("languageconfig", main.OutputFile);
            Assert.Equal("language", main.OutputDir);
            Assert.Equal("key", main.KeyFieldName);
            Assert.Equal(@"Output\baseline\l10n.json", main.SidecarPath);

            Assert.False(spaces[2].IndexMode);
            // indexMode=false 时 OutputFile 有默认值
            Assert.Equal("languageconfig", spaces[2].OutputFile);
        }

        [Fact]
        public void 未配置spaces_返回空表()
        {
            Assert.Empty(L10NSpaceParser.Parse(new Dictionary<string, string> { ["l10n.languages"] = "zh_CN" }));
        }

        [Fact]
        public void space缺省字段_给默认值()
        {
            var opts = new Dictionary<string, string>
            {
                ["l10n.spaces"] = "main",
                ["l10n.main.tables"] = "LanguageText",
                ["l10n.main.indexMode"] = "true",
            };
            var main = L10NSpaceParser.Parse(opts)[0];
            Assert.Equal("languageconfig", main.OutputFile);
            Assert.Equal("main", main.OutputDir);      // 默认 = space 名
            Assert.Equal("key", main.KeyFieldName);    // 默认 key
            Assert.Equal("zh_CN", main.KeyFieldDesc);  // 默认 zh_CN
            Assert.Empty(main.Languages);              // 未配则空(导出时跳过该 space 并 WARN)
        }

        [Fact]
        public void EnvManager空family按完整key命中_验证CollectL10NOptions的Probe语义()
        {
            // GenerationContext.CollectL10NOptions 的 Probe 依赖此语义:
            // xargs 以完整 key("-x l10n.main.tables=x")进入 EnvManager 的扁平字典,
            // family 传空串时 GetOptionOrDefault 按 name 原样查找即可精确命中;未命中返回默认值。
            var env = new EnvManager(new Dictionary<string, string>
            {
                ["l10n.main.tables"] = "LanguageText",
                ["l10n.spaces"] = "main",
            });
            Assert.Equal("main", env.GetOptionOrDefault("", "l10n.spaces", true, ""));
            Assert.Equal("LanguageText", env.GetOptionOrDefault("", "l10n.main.tables", true, ""));
            Assert.Equal("", env.GetOptionOrDefault("", "l10n.main.missing", true, ""));
        }

        [Fact]
        public void genAccessors_默认true_false关闭_大小写不敏感()
        {
            var opts = new Dictionary<string, string>
            {
                ["l10n.spaces"] = "main,aot,server",
                ["l10n.main.tables"] = "LanguageText",
                ["l10n.aot.tables"] = "LanguageAOT",
                ["l10n.server.tables"] = "LanguageServer",
                ["l10n.aot.genAccessors"] = "false",
                ["l10n.server.genAccessors"] = "FALSE",
            };
            var spaces = L10NSpaceParser.Parse(opts);
            Assert.True(spaces[0].GenAccessors);   // 未配置 = 默认 true(向后兼容)
            Assert.False(spaces[1].GenAccessors);  // "false"
            Assert.False(spaces[2].GenAccessors);  // 大小写不敏感
        }

        [Fact]
        public void genAccessors_非false的值保持true()
        {
            // 默认 true 的选项取"仅显式 false 关闭"语义:拼错/无关值不误伤(与默认 false 的 indexMode 相反)
            var opts = new Dictionary<string, string>
            {
                ["l10n.spaces"] = "main",
                ["l10n.main.tables"] = "LanguageText",
                ["l10n.main.genAccessors"] = "flase",
            };
            Assert.True(L10NSpaceParser.Parse(opts)[0].GenAccessors);

            opts["l10n.main.genAccessors"] = "true";
            Assert.True(L10NSpaceParser.Parse(opts)[0].GenAccessors);
        }
    }
}
