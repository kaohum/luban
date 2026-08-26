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
    /// DLP1 patch 字符串表格式测试（spec 2026-08-25，Task 3）：
    /// 管线两遍——1) baseline-with-sidecar 出基准（写 sidecar）；
    /// 2) 改 CSV（改一行 desc + 新增一行）后 incremental 出 patch。
    /// 断言 patch 为 [DLP1][sig][字符串表][upsertCount][记录索引][deleteCount][delete 索引] 布局。
    /// 与 ExportOnlyFieldTests 同 Collection：管线写进程级单例（GenerationContext.GlobalConf /
    /// EnvManager.Current / 各 *Manager），并行 collection 会互踩，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class DLP1StringTableFormatTests : IDisposable
    {
        private readonly string _root;

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

        public DLP1StringTableFormatTests()
        {
            // 与 Program.SetupApp 一致：ExcelDataReader 等加载器依赖 CodePages 编码 provider（进程内一次性注册）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-dlp1-" + Guid.NewGuid().ToString("N"));
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

        private static string Csv(string desc10002, string extraRow)
        {
            return "##var,id,name,desc\n" +
                "##,语言id,访问器名,描述\n" +
                "##type,int,string,string\n" +
                ",10001,btn_ok,通用按钮\n" +
                $",10002,btn_cancel,{desc10002}\n" +
                extraRow;
        }

        /// <summary>物化 mini conf 并跑 default 管线（dataExporter 指定导出器），返回 bin 输出目录。</summary>
        private string RunExport(string sub, string dataExporter, string sidecar, string desc10002, string csvExtraRow)
        {
            string dir = Path.Combine(_root, sub);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), SchemaXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "lang.csv"), Csv(desc10002, csvExtraRow).Replace("\n", "\r\n"), new UTF8Encoding(false));

            var conf = new StringBuilder();
            conf.AppendLine("{");
            conf.AppendLine("  \"groups\": [{\"names\":[\"all\",\"c\"], \"default\":true}],");
            conf.AppendLine("  \"schemaFiles\": [{\"fileName\":\"defines/__tables__.xml\", \"type\":\"\"}],");
            conf.AppendLine("  \"dataDir\": \"Datas\",");
            conf.AppendLine("  \"targets\": [{\"name\":\"mini\", \"manager\":\"Tables\", \"groups\":[\"all\",\"c\"], \"topModule\":\"Mini\"}],");
            conf.AppendLine("  \"xargs\": [");
            conf.AppendLine($"    \"outputCodeDir={codeDir}\",");
            conf.AppendLine($"    \"bin.outputDataDir={binDir}\",");
            conf.AppendLine($"    \"dataExporter={dataExporter}\",");
            conf.AppendLine($"    \"incremental.sidecarPath={sidecar.Replace('\\', '/')}\",");
            conf.AppendLine($"    \"incremental.exportStamp=1700000000\"");
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
        public void DLP1_头部字符串表_upsert索引_delete主键索引()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            // 1) 基准：baseline-with-sidecar 产 .bytes + sidecar（行 MD5 保持内联）
            RunExport("base", "baseline-with-sidecar", sidecar, "通用按钮", "");
            // 2) 增量：改 10002 的 desc + 新增 10034 行
            string deltaDir = RunExport("delta", "incremental", sidecar, "新的描述", ",10034,mail_title,邮件标题\n");

            string patch = Path.Combine(deltaDir, "LanguageText.patch.bytes");
            Assert.True(File.Exists(patch), "patch 文件缺失");

            var buf = ByteBuf.Wrap(File.ReadAllBytes(patch));

            // magic DLP1（4 字节 ASCII）
            var magic = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                magic[i] = buf.ReadByte();
            }
            Assert.Equal("DLP1", Encoding.UTF8.GetString(magic));
            // signatureId（schema 结构签名）
            Assert.False(string.IsNullOrEmpty(buf.ReadString()));

            // 字符串表：[count][len×count][blob]
            int sc = buf.ReadSize();
            Assert.True(sc > 0, $"patch 字符串表应非空，实际 {sc}");
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
            Assert.Contains("新的描述", table); // 改动行的新 desc
            Assert.Contains("邮件标题", table); // 新增行的 desc

            // upsert 记录：id(int) + name(index) + desc(index)
            int upsertCount = buf.ReadSize();
            Assert.Equal(2, upsertCount); // 改动行 10002 + 新增行 10034
            var upserts = new Dictionary<int, (string Name, string Desc)>();
            for (int i = 0; i < upsertCount; i++)
            {
                int id = buf.ReadInt();
                string name = table[buf.ReadSize()];
                string desc = table[buf.ReadSize()];
                upserts[id] = (name, desc);
            }
            Assert.Equal("btn_cancel", upserts[10002].Name);
            Assert.Equal("新的描述", upserts[10002].Desc);
            Assert.Equal("mail_title", upserts[10034].Name);
            Assert.Equal("邮件标题", upserts[10034].Desc);

            // delete 段（本测试无删除行为 0）
            int deleteCount = buf.ReadSize();
            Assert.Equal(0, deleteCount);
            Assert.Equal(0, buf.Remaining);
        }
    }
}
