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
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR ANY OTHER DEALINGS IN THE
// SOFTWARE.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Luban.Pipeline;
using Luban.Schema;
using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// 进程内跑 default 管线的测试共用串行 Collection（管线写 GenerationContext.GlobalConf /
    /// EnvManager.Current / 各 *Manager 等进程级单例，跨类并行会互踩）。
    /// </summary>
    [CollectionDefinition("PipelineSerial")]
    public class PipelineSerialCollection
    {
    }

    /// <summary>
    /// AF-2 字段级 tags 标记 export_only：
    /// 字段 schema 存在、数据加载照常填充（管线如 keyFlag 行级过滤仍可读），
    /// 但不进入任何数据序列化（bin/json），也不生成运行时读写代码（cs-bin）。
    /// 与正式仓 language_l10n.xml 的 is_code 同构（语言列带 group，zh_CN/en_US 不随目标导出）。
    /// 与 CsharpL10NLanguageCodegenTests 同 Collection：管线会写进程级单例，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class ExportOnlyFieldTests : IDisposable
    {
        private readonly string _root;

        private const string LangCsv =
            "##var,id,name,is_code,zh_CN,en_US\n" +
            "##,语言id,访问器名,代码引用,简体中文,英文\n" +
            "##type,int,string,bool,string,string\n" +
            ",10001,btn_ok,true,确定,OK\n" +
            ",10002,btn_cancel,true,取消,Cancel\n" +
            ",10034,mail_title,,邮件标题,Mail Title\n";

        /// <summary>marker=false = 当前正式仓 xml 原样（无 tags）——向后兼容基线。</summary>
        private static string BuildSchema(bool marker)
        {
            string isCode = marker
                ? "        <var name=\"is_code\" type=\"bool\" tags=\"export_only\"/>\n"
                : "        <var name=\"is_code\" type=\"bool\"/>\n";
            return "<root>\n" +
                "    <bean name=\"Language\" valueType=\"true\">\n" +
                "        <var name=\"id\" type=\"int\"/>\n" +
                "        <var name=\"name\" type=\"string\"/>\n" +
                isCode +
                "        <var name=\"zh_CN\" type=\"string\" group=\"zh_CN\"/>\n" +
                "        <var name=\"en_US\" type=\"string\" group=\"en_US\"/>\n" +
                "    </bean>\n" +
                "    <table name=\"LanguageText\" mode=\"map\" value=\"Language\" input=\"lang.csv\">\n" +
                "        <index name=\"Id\" field=\"id\"/>\n" +
                "    </table>\n" +
                "</root>\n";
        }

        public ExportOnlyFieldTests()
        {
            // 与 Program.SetupApp 一致：ExcelDataReader 等加载器依赖 CodePages 编码 provider（进程内一次性注册）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-export-only-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>
        /// 物化 mini conf 并跑 default 管线。codeTargets/dataTargets 指定目标，
        /// 输出目录固定 {dir}/Output/code、{dir}/Output/data-bin、{dir}/Output/data-json。
        /// extraXargs 追加到 xargs 尾部（覆盖同名项语义靠 EnvManager 后写优先）。
        /// </summary>
        private string MaterializeAndRun(string name, bool marker, string[] codeTargets, string[] dataTargets,
            params string[] extraXargs)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            string jsonDir = Path.Combine(dir, "Output", "data-json").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), BuildSchema(marker), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "lang.csv"), LangCsv.Replace("\n", "\r\n"), new UTF8Encoding(false));

            var xargs = new List<string>
            {
                $"outputCodeDir={codeDir}",
                $"bin.outputDataDir={binDir}",
                $"json.outputDataDir={jsonDir}",
            };
            xargs.AddRange(extraXargs);

            var conf = new StringBuilder();
            conf.AppendLine("{");
            conf.AppendLine("  \"groups\": [{\"names\":[\"all\",\"c\"], \"default\":true}],");
            conf.AppendLine("  \"schemaFiles\": [{\"fileName\":\"defines/__tables__.xml\", \"type\":\"\"}],");
            conf.AppendLine("  \"dataDir\": \"Datas\",");
            conf.AppendLine("  \"targets\": [{\"name\":\"mini\", \"manager\":\"Tables\", \"groups\":[\"all\",\"c\"], \"topModule\":\"Mini\"}],");
            conf.AppendLine("  \"xargs\": [");
            conf.Append(string.Join(",\n", xargs.Select(x => "    \"" + x.Replace("\\", "\\\\") + "\"")));
            conf.AppendLine("\n  ]");
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
                CodeTargets = codeTargets.ToList(),
                DataTargets = dataTargets.ToList(),
                IncludeTags = new List<string>(),
                ExcludeTags = new List<string>(),
                Variants = new Dictionary<string, string>(),
                OutputTables = new List<string>(),
            });
            return dir;
        }

        private static string ReadText(string dir, params string[] parts)
        {
            return File.ReadAllText(Path.Combine(new[] { dir }.Concat(parts).ToArray()), Encoding.UTF8);
        }

        /// <summary>解 bin：签名串 + count + 每行 [id:int, name:string, (有标记字段时) isCode:bool]；返回逐行结果。</summary>
        private static (string Signature, List<(int Id, string Name, bool? IsCode)> Rows) ParseBin(byte[] bytes, bool expectBool)
        {
            var buf = new ByteBuf(bytes);
            string signature = buf.ReadString();
            int count = buf.ReadSize();
            var rows = new List<(int, string, bool?)>();
            for (int i = 0; i < count; i++)
            {
                int id = buf.ReadInt();
                string name = buf.ReadString();
                bool? isCode = expectBool ? buf.ReadBool() : null;
                rows.Add((id, name, isCode));
            }
            Assert.Equal(0, buf.Remaining);
            return (signature, rows);
        }

        /// <summary>去掉签名头后的数据体长度（签名 hex 长度本身 31~32 抖动，不参与逐字节对比）。</summary>
        private static int PayloadLengthAfterSignature(byte[] bytes)
        {
            var buf = new ByteBuf(bytes);
            buf.ReadString();
            return bytes.Length - buf.ReaderIndex;
        }

        [Fact]
        public void 无标记_旧行为_序列化is_code且生成读写代码()
        {
            string dir = MaterializeAndRun("baseline", marker: false,
                new[] { "cs-bin" }, new[] { "bin", "json" });

            // cs-bin：字段成员 + 反序列化读 bool（旧行为）
            string languageCs = ReadText(dir, "Output", "code", "Language.cs");
            Assert.Contains("public readonly bool IsCode;", languageCs);
            Assert.Contains("IsCode = _buf.ReadBool();", languageCs);

            // bin：每行 4B id + name + 1B bool；bool 值逐行可见
            byte[] bin = File.ReadAllBytes(Path.Combine(dir, "Output", "data-bin", "languagetext.bytes"));
            var (_, rows) = ParseBin(bin, expectBool: true);
            Assert.Equal(new[] { 10001, 10002, 10034 }, rows.Select(r => r.Id));
            Assert.Equal(new[] { true, true, false }, rows.Select(r => r.IsCode.Value));

            // json：is_code 属性随行写出（默认非紧凑格式，冒号后带空格）
            string json = ReadText(dir, "Output", "data-json", "languagetext.json");
            Assert.Contains("\"is_code\": true", json);
            Assert.Contains("\"is_code\": false", json);
        }

        [Fact]
        public void 有标记_bin无bool字节_json无属性_代码无读写()
        {
            string dir = MaterializeAndRun("marker", marker: true,
                new[] { "cs-bin" }, new[] { "bin", "json" });

            // cs-bin：无 IsCode 成员、无 ReadBool
            string languageCs = ReadText(dir, "Output", "code", "Language.cs");
            Assert.DoesNotContain("IsCode", languageCs);
            Assert.DoesNotContain("ReadBool", languageCs);
            Assert.Contains("Id = _buf.ReadInt();", languageCs);
            Assert.Contains("Name = _buf.ReadString();", languageCs);

            // bin：按"无 bool"布局逐字节恰好消费完（若残留 bool 字节，name 读串位/Remaining 断言必炸）
            byte[] bin = File.ReadAllBytes(Path.Combine(dir, "Output", "data-bin", "languagetext.bytes"));
            var (_, rows) = ParseBin(bin, expectBool: false);
            Assert.Equal(new[] { 10001, 10002, 10034 }, rows.Select(r => r.Id));
            Assert.Equal(new[] { "btn_ok", "btn_cancel", "mail_title" }, rows.Select(r => r.Name));

            // json：无 is_code 属性（zh_CN/en_US 为 group 过滤列，两态都不出现，顺带校验）
            string json = ReadText(dir, "Output", "data-json", "languagetext.json");
            Assert.DoesNotContain("is_code", json);
            Assert.DoesNotContain("zh_CN", json);
        }

        [Fact]
        public void 有无标记_bin数据体恰好每行差1字节_签名不同()
        {
            // bool = 1 字节/行；行数 3 => 数据体差 3 字节（签名头 hex 长度 31~32 抖动，剥离后对比）。
            // 签名头不同：export_only 参与 StructureSignature（序列化形状变了，SignatureId 必须变）。
            string dirOff = MaterializeAndRun("len-off", marker: false, new[] { "cs-bin" }, new[] { "bin" });
            string dirOn = MaterializeAndRun("len-on", marker: true, new[] { "cs-bin" }, new[] { "bin" });

            byte[] binOff = File.ReadAllBytes(Path.Combine(dirOff, "Output", "data-bin", "languagetext.bytes"));
            byte[] binOn = File.ReadAllBytes(Path.Combine(dirOn, "Output", "data-bin", "languagetext.bytes"));
            Assert.Equal(PayloadLengthAfterSignature(binOn) + 3, PayloadLengthAfterSignature(binOff));

            var (sigOff, _) = ParseBin(binOff, expectBool: true);
            var (sigOn, _) = ParseBin(binOn, expectBool: false);
            Assert.NotEqual(sigOff, sigOn);
        }

        [Fact]
        public void 有标记_keyFlag行级过滤仍可读_is_code驱动访问器()
        {
            // 管线可读性：is_code 带 export_only 标记后，cs-l10n-language 的 keyFlag
            // 仍从加载行读值——只有 is_code=true 的行生成静态访问器（AF-2 与 AF-3 的交点）。
            string dir = MaterializeAndRun("keyflag", marker: true,
                new[] { "cs-l10n-language" }, new string[0],
                "l10n.spaces=main",
                "l10n.main.tables=LanguageText",
                "l10n.main.indexMode=true",
                "l10n.main.languages=zh_CN,en_US",
                "l10n.main.outputFile=languageconfig",
                "l10n.main.outputDir=language",
                "l10n.main.keyFieldName=id",
                "l10n.main.keyFieldDesc=zh_CN",
                "cs-l10n-language.space.main.keyFlag=is_code");

            string accessor = ReadText(dir, "Output", "code", "LanguageConfig.cs");
            Assert.Contains("public static string btn_ok => Get(10001);", accessor);
            Assert.Contains("public static string btn_cancel => Get(10002);", accessor);
            Assert.DoesNotContain("mail_title", accessor);
        }
    }
}
