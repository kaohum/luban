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
    /// DLF1 整表替换 patch 测试：
    /// LIST 表 index="qualityId,mainCityLv"（两个独立索引、数据上键均重复 -> 无单值身份索引），
    /// 基准后 sidecar 标记整表替换模式（IdentityIndex 空、行哈希用记录序号）；内容变化时出 DLF1 全表 patch
    /// （magic + 与全表 .bytes 完全一致的 body），无变化不出 patch。
    /// 与 ExportOnlyFieldTests 同 Collection：管线写进程级单例，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class DLF1ReplacePatchTests : IDisposable
    {
        private readonly string _root;

        private const string SchemaXml =
            "<root>\n" +
            "    <bean name=\"Trade\" valueType=\"true\">\n" +
            "        <var name=\"qualityId\" type=\"int\"/>\n" +
            "        <var name=\"mainCityLv\" type=\"int\"/>\n" +
            "        <var name=\"count\" type=\"int\"/>\n" +
            "    </bean>\n" +
            "    <table name=\"TradeConfig\" mode=\"list\" value=\"Trade\" input=\"trade.csv\" index=\"qualityId,mainCityLv\"/>\n" +
            "</root>\n";

        public DLF1ReplacePatchTests()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-dlf1-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>qualityId（1,1,2）与 mainCityLv（10,20,10）均有重复，无单值身份索引。</summary>
        private static string BaseCsv()
        {
            return "##var,qualityId,mainCityLv,count\n" +
                "##,品质,主城等级,数量\n" +
                "##type,int,int,int\n" +
                ",1,10,5\n" +
                ",1,20,6\n" +
                ",2,10,7\n";
        }

        private static string ChangedCsv()
        {
            return "##var,qualityId,mainCityLv,count\n" +
                "##,品质,主城等级,数量\n" +
                "##type,int,int,int\n" +
                ",1,10,5\n" +
                ",1,20,66\n" +
                ",2,10,7\n";
        }

        private string RunExport(string sub, string dataExporter, string sidecar, string csvContent)
        {
            string dir = Path.Combine(_root, sub);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), SchemaXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "trade.csv"), csvContent.Replace("\n", "\r\n"), new UTF8Encoding(false));

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

        [Fact]
        public void 基准sidecar标记整表替换模式_行哈希用记录序号()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            RunExport("base", "baseline-with-sidecar", sidecar, BaseCsv());

            using var doc = JsonDocument.Parse(File.ReadAllText(sidecar));
            var entry = doc.RootElement.GetProperty("Tables").GetProperty("TradeConfig");
            Assert.Equal("", entry.GetProperty("IdentityIndex").GetString());
            Assert.Equal(3, entry.GetProperty("RowCount").GetInt32());
            var keys = new List<string>();
            foreach (var prop in entry.GetProperty("RowHashes").EnumerateObject())
            {
                keys.Add(prop.Name);
            }
            Assert.Equal(3, keys.Count);
            Assert.Contains("0", keys);
            Assert.Contains("1", keys);
            Assert.Contains("2", keys);
        }

        [Fact]
        public void 内容变化出DLF1整表替换patch_body与全表bytes同构()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            RunExport("base", "baseline-with-sidecar", sidecar, BaseCsv());
            string binDir = RunExport("delta", "incremental", sidecar, ChangedCsv());

            string patch = Path.Combine(binDir, "tradeconfig.patch.bytes");
            Assert.True(File.Exists(patch), "patch 文件缺失");

            var buf = ByteBuf.Wrap(File.ReadAllBytes(patch));
            var magic = new byte[4];
            for (int i = 0; i < 4; i++)
            {
                magic[i] = buf.ReadByte();
            }
            Assert.Equal("DLF1", Encoding.UTF8.GetString(magic));

            // body 与全表 .bytes 同构：[sig][字符串表][count][rows]，客户端可复用 Create 解析
            Assert.False(string.IsNullOrEmpty(buf.ReadString()));
            int sc = buf.ReadSize();
            Assert.Equal(0, sc); // bean 无 string 字段
            int rowCount = buf.ReadSize();
            Assert.Equal(3, rowCount);
            var counts = new Dictionary<(int, int), int>();
            for (int i = 0; i < rowCount; i++)
            {
                int qualityId = buf.ReadInt();
                int mainCityLv = buf.ReadInt();
                int count = buf.ReadInt();
                counts[(qualityId, mainCityLv)] = count;
            }
            Assert.Equal(66, counts[(1, 20)]);
            Assert.Equal(5, counts[(1, 10)]);
            Assert.Equal(7, counts[(2, 10)]);
            Assert.Equal(0, buf.Remaining);

            // manifest：单条目，整表口径 UpsertCount=行数、DeleteCount=0
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(binDir, "_delta.manifest")));
            var tables = manifest.RootElement.GetProperty("ChangedTables");
            Assert.Equal(1, tables.GetArrayLength());
            var tableEntry = tables[0];
            Assert.Equal("TradeConfig", tableEntry.GetProperty("Table").GetString());
            Assert.Equal(3, tableEntry.GetProperty("UpsertCount").GetInt32());
            Assert.Equal(0, tableEntry.GetProperty("DeleteCount").GetInt32());
        }

        [Fact]
        public void 无变化不出patch_manifest为空()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            RunExport("base", "baseline-with-sidecar", sidecar, BaseCsv());
            string binDir = RunExport("delta", "incremental", sidecar, BaseCsv());

            Assert.False(File.Exists(Path.Combine(binDir, "tradeconfig.patch.bytes")), "无变化不应产出 patch");
            using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(binDir, "_delta.manifest")));
            Assert.Equal(0, manifest.RootElement.GetProperty("ChangedTables").GetArrayLength());
        }
    }
}
