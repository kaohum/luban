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
using System.Text;
using Luban.Pipeline;
using Luban.Schema;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// typescript-bin 二进制字符串表化代码生成形状测试（spec 2026-08-25，Task 14）：
    /// string 字段反序列化发射从 `readString()` 改为 `readStringIndex()`（记录内索引引用），
    /// 表类构造入口先读头部字符串表 `readStringTable()`（bean 构造不变）。
    /// 进程内跑 default 管线（temp mini conf，map 表 LanguageText，csv 含重复字符串），
    /// 断言生成的 schema.ts：bean string 字段走 readStringIndex()；表构造含 readStringTable()。
    /// 与 ExportOnlyFieldTests 同 Collection：管线写进程级单例（GenerationContext.GlobalConf /
    /// EnvManager.Current / 各 *Manager），并行 collection 会互踩，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class TypescriptBinStringTableCodegenTests : IDisposable
    {
        private readonly string _root;

        private const string LangCsv =
            "##var,id,name,desc\n" +
            "##,语言id,访问器名,描述\n" +
            "##type,int,string,string\n" +
            ",10001,btn_ok,通用按钮\n" +
            ",10002,btn_cancel,通用按钮\n" +
            ",10034,mail_title,邮件标题\n";

        private const string SchemaXml =
            "<root>\n" +
            "    <bean name=\"Language\" valueType=\"true\">\n" +
            "        <var name=\"id\" type=\"int\"/>\n" +
            "        <var name=\"name\" type=\"string\"/>\n" +
            "        <var name=\"desc\" type=\"string\"/>\n" +
            "    </bean>\n" +
            "    <table name=\"LanguageText\" mode=\"map\" value=\"Language\" input=\"lang.csv\">\n" +
            "        <index name=\"Id\" field=\"id\"/>\n" +
            "    </table>\n" +
            "</root>\n";

        public TypescriptBinStringTableCodegenTests()
        {
            // 与 Program.SetupApp 一致：ExcelDataReader 等加载器依赖 CodePages 编码 provider（进程内一次性注册）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-ts-bin-st-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_root, true);
            }
            catch (IOException)
            {
            }
        }

        /// <summary>物化 mini conf 并跑 default 管线（typescript-bin 代码目标 + bin 数据目标），返回 conf 目录。</summary>
        private string MaterializeAndRun()
        {
            string dir = Path.Combine(_root, "t");
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), SchemaXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "lang.csv"), LangCsv.Replace("\n", "\r\n"), new UTF8Encoding(false));

            var conf = new StringBuilder();
            conf.AppendLine("{");
            conf.AppendLine("  \"groups\": [{\"names\":[\"all\",\"c\"], \"default\":true}],");
            conf.AppendLine("  \"schemaFiles\": [{\"fileName\":\"defines/__tables__.xml\", \"type\":\"\"}],");
            conf.AppendLine("  \"dataDir\": \"Datas\",");
            conf.AppendLine("  \"targets\": [{\"name\":\"mini\", \"manager\":\"Tables\", \"groups\":[\"all\",\"c\"], \"topModule\":\"Mini\"}],");
            conf.AppendLine("  \"xargs\": [");
            conf.AppendLine($"    \"outputCodeDir={codeDir}\",");
            conf.AppendLine($"    \"bin.outputDataDir={binDir}\"");
            conf.AppendLine("  ]");
            conf.AppendLine("}");
            File.WriteAllText(Path.Combine(dir, "luban.conf"), conf.ToString(), new UTF8Encoding(false));

            var loader = new GlobalConfigLoader();
            var config = loader.Load(Path.Combine(dir, "luban.conf"));
            GenerationContext.GlobalConf = config;

            var options = new Dictionary<string, string>();
            foreach (var xarg in config.Xargs)
            {
                string[] pair = xarg.Split('=', 2);
                options[pair[0]] = pair[1];
            }
            var launcher = new SimpleLauncher();
            launcher.Start(options);

            var pipeline = PipelineManager.Ins.CreatePipeline("default");
            pipeline.Run(new PipelineArguments
            {
                Target = "mini",
                SchemaCollector = "default",
                Config = config,
                CodeTargets = new List<string> { "typescript-bin" },
                DataTargets = new List<string> { "bin" },
                IncludeTags = new List<string>(),
                ExcludeTags = new List<string>(),
                Variants = new Dictionary<string, string>(),
                OutputTables = new List<string>(),
            });
            return dir;
        }

        [Fact]
        public void typescriptBin_生成代码_string为readStringIndex_表构造读字符串表()
        {
            string dir = MaterializeAndRun();

            // typescript-bin 全合一输出 schema.ts（bean 与表类同文件）
            string schema = File.ReadAllText(Path.Combine(dir, "Output", "code", "schema.ts"), Encoding.UTF8);

            // string 字段反序列化在 bean 构造器里发射：name/desc 两字段均应走 readStringIndex()
            Assert.Contains("readStringIndex()", schema);
            Assert.DoesNotContain("readString()", schema);

            // 表类构造入口先读头部字符串表（记录内 string 是索引引用，必须先 readStringTable）
            Assert.Contains("readStringTable()", schema);
        }
    }
}
