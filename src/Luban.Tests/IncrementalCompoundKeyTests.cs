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
using System.Text.Json;
using Luban.Pipeline;
using Luban.Schema;
using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// 复合身份索引行级 diff 测试：
    /// LIST 表 index="techTypeId+level"（首字段不唯一，旧实现按首字段取键会碰撞跳过），
    /// 基准后 sidecar 记录 IdentityIndex 与全键行哈希；改行/删行/加行后 DLP1 patch 按 "techTypeId+level" 全键 diff。
    /// 与 ExportOnlyFieldTests 同 Collection：管线写进程级单例，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class IncrementalCompoundKeyTests : IDisposable
    {
        private readonly string _root;

        private const string SchemaXml =
            "<root>\n" +
            "    <bean name=\"Tech\" valueType=\"true\">\n" +
            "        <var name=\"techTypeId\" type=\"int\"/>\n" +
            "        <var name=\"level\" type=\"int\"/>\n" +
            "        <var name=\"weight\" type=\"int\"/>\n" +
            "    </bean>\n" +
            "    <table name=\"TechLevel\" mode=\"list\" value=\"Tech\" input=\"tech.csv\" index=\"techTypeId+level\"/>\n" +
            "</root>\n";

        public IncrementalCompoundKeyTests()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-compound-key-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>techTypeId 重复出现（101 两行），首字段不唯一。</summary>
        private static string BaseCsv()
        {
            return "##var,techTypeId,level,weight\n" +
                "##,科技类型,等级,权重\n" +
                "##type,int,int,int\n" +
                ",101,1,10\n" +
                ",101,2,20\n" +
                ",102,1,30\n";
        }

        /// <summary>101+2 权重 20->99（改）、102+1 移除（删）、103+1 新增（加）。</summary>
        private static string DeltaCsv()
        {
            return "##var,techTypeId,level,weight\n" +
                "##,科技类型,等级,权重\n" +
                "##type,int,int,int\n" +
                ",101,1,10\n" +
                ",101,2,99\n" +
                ",103,1,50\n";
        }

        /// <summary>物化 mini conf 并跑 default 管线（dataExporter 指定导出器、csv 为全文内容），返回 bin 输出目录。</summary>
        private string RunExport(string sub, string dataExporter, string sidecar, string csvContent)
        {
            string dir = Path.Combine(_root, sub);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), SchemaXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "tech.csv"), csvContent.Replace("\n", "\r\n"), new UTF8Encoding(false));

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
            conf.AppendLine("    \"incremental.exportStamp=1700000000\"");
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

        private static List<string> RowHashKeys(JsonElement entry)
        {
            var names = new List<string>();
            foreach (var prop in entry.GetProperty("RowHashes").EnumerateObject())
            {
                names.Add(prop.Name);
            }
            return names;
        }

        [Fact]
        public void 基准sidecar记录复合全键与身份索引()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            RunExport("base", "baseline-with-sidecar", sidecar, BaseCsv());

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            Assert.Equal(1, doc.RootElement.GetProperty("Version").GetInt32());
            var entry = doc.RootElement.GetProperty("Tables").GetProperty("TechLevel");
            Assert.Equal("techTypeId+level", entry.GetProperty("IdentityIndex").GetString());
            Assert.Equal(3, entry.GetProperty("RowCount").GetInt32());
            var keys = RowHashKeys(entry);
            Assert.Equal(3, keys.Count);
            Assert.Contains("101+1", keys);
            Assert.Contains("101+2", keys);
            Assert.Contains("102+1", keys);
        }

        [Fact]
        public void 改行加行走DLP1行级diff_删行走全键delete()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            RunExport("base", "baseline-with-sidecar", sidecar, BaseCsv());
            string binDir = RunExport("delta", "incremental", sidecar, DeltaCsv());

            string patch = Path.Combine(binDir, "techlevel.patch.bytes");
            Assert.True(File.Exists(patch), "patch 文件缺失");

            var buf = ByteBuf.Wrap(File.ReadAllBytes(patch));
            var magic = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                magic[i] = buf.ReadByte();
            }
            Assert.Equal("DLP1", Encoding.UTF8.GetString(magic));
            Assert.False(string.IsNullOrEmpty(buf.ReadString()));

            // 字符串表：bean 无 string 字段，唯一注册的是 delete 键 "102+1"
            int sc = buf.ReadSize();
            Assert.Equal(1, sc);
            var lens = new int[sc];
            var table = new string[sc];
            for (int i = 0; i < sc; i++)
            {
                lens[i] = buf.ReadSize();
            }
            for (int i = 0; i < sc; i++)
            {
                var bytes = new byte[lens[i]];
                for (int j = 0; j < lens[i]; j++)
                {
                    bytes[j] = buf.ReadByte();
                }
                table[i] = Encoding.UTF8.GetString(bytes);
            }
            Assert.Equal("102+1", table[0]);

            // upsert：101+2（weight 99）与 103+1（weight 50）
            int upsertCount = buf.ReadSize();
            Assert.Equal(2, upsertCount);
            var upserts = new Dictionary<(int, int), int>();
            for (int i = 0; i < upsertCount; i++)
            {
                int techTypeId = buf.ReadInt();
                int level = buf.ReadInt();
                int weight = buf.ReadInt();
                upserts[(techTypeId, level)] = weight;
            }
            Assert.Equal(99, upserts[(101, 2)]);
            Assert.Equal(50, upserts[(103, 1)]);

            // delete：102+1 全键（字符串表索引 0）
            int deleteCount = buf.ReadSize();
            Assert.Equal(1, deleteCount);
            Assert.Equal(0, buf.ReadSize());
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void 旧格式sidecar被增量导出拒绝()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            RunExport("base", "baseline-with-sidecar", sidecar, BaseCsv());

            // 抹掉 Version 字段模拟旧格式 sidecar
            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartObject();
                foreach (var prop in doc.RootElement.EnumerateObject())
                {
                    if (prop.Name != "Version")
                    {
                        prop.WriteTo(writer);
                    }
                }
                writer.WriteEndObject();
            }
            File.WriteAllText(sidecar, Encoding.UTF8.GetString(ms.ToArray()));

            var ex = Assert.ThrowsAny<Exception>(() => RunExport("delta", "incremental", sidecar, DeltaCsv()));
            Assert.Contains("旧格式", ex.Message);
        }
    }
}
