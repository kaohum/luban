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
using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// bin 字符串表格式测试（spec 2026-08-25，Task 2）：
    /// 进程内跑 default 管线（mini conf，cs-bin + bin），断言输出 .bytes 为
    /// [signature][字符串表][recordCount][records(索引引用)] 布局。
    /// 与 ExportOnlyFieldTests 同 Collection：管线写进程级单例（GenerationContext.GlobalConf /
    /// EnvManager.Current / 各 *Manager），并行 collection 会互踩，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class BinStringTableFormatTests : IDisposable
    {
        private readonly string _root;

        private const string LangCsv =
            "##var,id,name,desc,zh_CN,en_US\n" +
            "##,语言id,访问器名,描述,简体中文,英文\n" +
            "##type,int,string,string,string,string\n" +
            ",10001,btn_ok,通用按钮,确定,OK\n" +
            ",10002,btn_cancel,通用按钮,取消,Cancel\n" +
            ",10034,mail_title,邮件标题,邮件标题,Mail Title\n";

        private const string SchemaXml =
            "<root>\n" +
            "    <bean name=\"Language\" valueType=\"true\">\n" +
            "        <var name=\"id\" type=\"int\"/>\n" +
            "        <var name=\"name\" type=\"string\"/>\n" +
            "        <var name=\"desc\" type=\"string\"/>\n" +
            "        <var name=\"zh_CN\" type=\"string\" group=\"zh_CN\"/>\n" +
            "        <var name=\"en_US\" type=\"string\" group=\"en_US\"/>\n" +
            "    </bean>\n" +
            "    <table name=\"LanguageText\" mode=\"map\" value=\"Language\" input=\"lang.csv\">\n" +
            "        <index name=\"Id\" field=\"id\"/>\n" +
            "    </table>\n" +
            "</root>\n";

        public BinStringTableFormatTests()
        {
            // 与 Program.SetupApp 一致：ExcelDataReader 等加载器依赖 CodePages 编码 provider（进程内一次性注册）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-bin-st-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>物化 mini conf 并跑 default 管线（cs-bin + bin），返回 bin 输出目录。</summary>
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
                CodeTargets = new List<string> { "cs-bin" },
                DataTargets = new List<string> { "bin" },
                IncludeTags = new List<string>(),
                ExcludeTags = new List<string>(),
                Variants = new Dictionary<string, string>(),
                OutputTables = new List<string>(),
            });
            return binDir;
        }

        [Fact]
        public void 全量表_字符串表头部_索引引用()
        {
            string binDir = MaterializeAndRun();
            string file = Path.Combine(binDir, "LanguageText.bytes");
            Assert.True(File.Exists(file), "导出文件缺失");

            var buf = ByteBuf.Wrap(File.ReadAllBytes(file));
            string sig = buf.ReadString();
            Assert.False(string.IsNullOrEmpty(sig));

            // 字符串表：[count][len×count][blob]
            int sc = buf.ReadSize();
            Assert.True(sc >= 5, $"字符串表应含 id外的全部字符串，实际 {sc}");
            var lens = new int[sc];
            var table = new string[sc];
            for (int i = 0; i < sc; i++)
            {
                lens[i] = buf.ReadSize();
            }
            for (int i = 0; i < sc; i++)
            {
                if (lens[i] == 0)
                {
                    table[i] = "";
                    continue;
                }
                var bytes = new byte[lens[i]];
                for (int j = 0; j < lens[i]; j++)
                {
                    bytes[j] = buf.ReadByte();
                }
                table[i] = Encoding.UTF8.GetString(bytes);
            }
            Assert.Contains("通用按钮", table); // 去重后只出现一次
            Assert.Contains("btn_ok", table);

            // 记录区：[count][records…]，每条 id(int) + name(index) + desc(index)
            int n = buf.ReadSize();
            Assert.Equal(3, n);
            var rows = new Dictionary<int, (string Name, string Desc)>();
            for (int i = 0; i < n; i++)
            {
                int id = buf.ReadInt();
                string name = table[buf.ReadSize()];
                string desc = table[buf.ReadSize()];
                rows[id] = (name, desc);
            }
            Assert.Equal("btn_ok", rows[10001].Name);
            Assert.Equal("通用按钮", rows[10001].Desc);
            Assert.Equal(0, buf.Remaining);
        }
    }
}
