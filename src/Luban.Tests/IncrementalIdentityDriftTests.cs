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
    /// 身份索引漂移 gate 测试：
    /// 基准时 index="qualityId,itemId" 的 qualityId 数据唯一（身份索引=qualityId），
    /// 增量数据让 qualityId 出现重复（身份索引漂移为 itemId）——客户端代码与行键口径会分叉，
    /// 增量导出必须整批中止并要求重跑基准，而不是按新口径产 patch。
    /// 与 ExportOnlyFieldTests 同 Collection：管线写进程级单例，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class IncrementalIdentityDriftTests : IDisposable
    {
        private readonly string _root;

        private const string SchemaXml =
            "<root>\n" +
            "    <bean name=\"Item\" valueType=\"true\">\n" +
            "        <var name=\"qualityId\" type=\"int\"/>\n" +
            "        <var name=\"itemId\" type=\"int\"/>\n" +
            "        <var name=\"count\" type=\"int\"/>\n" +
            "    </bean>\n" +
            "    <table name=\"ItemConfig\" mode=\"list\" value=\"Item\" input=\"item.csv\" index=\"qualityId,itemId\"/>\n" +
            "</root>\n";

        public IncrementalIdentityDriftTests()
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-identity-drift-" + Guid.NewGuid().ToString("N"));
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

        private static string CsvWithRows(string extraRow)
        {
            return "##var,qualityId,itemId,count\n" +
                "##,品质,道具,数量\n" +
                "##type,int,int,int\n" +
                ",1,10,5\n" +
                ",2,20,6\n" +
                extraRow;
        }

        private void RunExport(string sub, string dataExporter, string sidecar, string csvContent)
        {
            string dir = Path.Combine(_root, sub);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), SchemaXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "item.csv"), csvContent.Replace("\n", "\r\n"), new UTF8Encoding(false));

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
        }

        [Fact]
        public void 身份索引漂移时增量导出整批中止()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            // 基准：qualityId（1,2）唯一 -> 身份索引 = qualityId
            RunExport("base", "baseline-with-sidecar", sidecar, CsvWithRows(""));

            // 增量：新增 (1,30) 使 qualityId 重复、itemId（10,20,30）唯一 -> 身份索引漂移为 itemId
            var ex = Assert.ThrowsAny<Exception>(() => RunExport("delta", "incremental", sidecar, CsvWithRows(",1,30,7\n")));
            Assert.Contains("身份索引", ex.Message);
            Assert.Contains("基准导出", ex.Message);
        }
    }
}
