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
using System.Text;
using Luban.Pipeline;
using Luban.Schema;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// cs-l10n-language v2（spec 2026-08-22 D7）代码生成形状测试：
    /// 进程内跑 default 管线（temp mini conf，schema=显式 int id + name 列），断言生成文件内容。
    /// 单形态：只出访问器 `public static string {name} => Get({id});`（id 烘焙、name 命名、
    /// Get(int)/dataMapRef 由各 space 手写 partial 提供）；server space 形态收敛且 id 不变；
    /// keyFlag 行级过滤与 name/id desync 守卫保持工作。
    /// 与 ExportOnlyFieldTests 同 Collection：管线会写进程级单例（GenerationContext.GlobalConf /
    /// EnvManager.Current / 各 *Manager），并行 collection 会互踩，必须串行。
    /// </summary>
    [Collection("PipelineSerial")]
    public class CsharpL10NLanguageCodegenTests : IDisposable
    {
        private readonly string _root;

        private const string DefaultLangCsv =
            "##var,id,name,is_code,zh_CN,en_US\n" +
            "##,语言id,访问器名,代码引用,简体中文,英文\n" +
            "##type,int,string,bool,string,string\n" +
            ",10001,btn_ok,true,确定,OK\n" +
            ",10002,btn_cancel,true,取消,Cancel\n" +
            ",10034,mail_title,,邮件标题,Mail Title\n" +
            ",20015,item_hero_name,,英雄名字,Hero Name\n";

        private const string DefaultLangServerCsv =
            "##var,id,zh_CN,en_US\n" +
            "##,语言id,简体中文,英文\n" +
            "##type,int,string,string\n" +
            ",1,服务器公告一,Server Notice 1\n" +
            ",2,服务器公告二,Server Notice 2\n" +
            ",3,邮件模板,Mail Template\n";

        private const string DefinesXml =
            "<root>\n" +
            "    <bean name=\"Language\" valueType=\"true\">\n" +
            "        <var name=\"id\" type=\"int\"/>\n" +
            "        <var name=\"name\" type=\"string\"/>\n" +
            "        <var name=\"is_code\" type=\"bool\"/>\n" +
            "        <var name=\"zh_CN\" type=\"string\"/>\n" +
            "        <var name=\"en_US\" type=\"string\"/>\n" +
            "    </bean>\n" +
            "    <bean name=\"LanguageServerEntry\" valueType=\"true\">\n" +
            "        <var name=\"id\" type=\"int\"/>\n" +
            "        <var name=\"zh_CN\" type=\"string\"/>\n" +
            "        <var name=\"en_US\" type=\"string\"/>\n" +
            "    </bean>\n" +
            "    <table name=\"LanguageText\" mode=\"map\" value=\"Language\" input=\"lang.csv\">\n" +
            "        <index name=\"Id\" field=\"id\"/>\n" +
            "    </table>\n" +
            "    <table name=\"LanguageServer\" mode=\"map\" value=\"LanguageServerEntry\" input=\"langserver.csv\">\n" +
            "        <index name=\"Id\" field=\"id\"/>\n" +
            "    </table>\n" +
            "</root>\n";

        public CsharpL10NLanguageCodegenTests()
        {
            // 与 Program.SetupApp 一致：ExcelDataReader 等加载器依赖 CodePages 编码 provider（进程内一次性注册）
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-cs-l10n-" + Guid.NewGuid().ToString("N"));
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

        /// <summary>物化一个 mini conf（main+server 两 space）；extraXargs 追加到 xargs 尾部（覆盖同名项语义靠 EnvManager 后写优先）。</summary>
        private string MaterializeConf(string name, string langCsv, string langServerCsv, params string[] extraXargs)
        {
            string dir = Path.Combine(_root, name);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string outputDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), DefinesXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "lang.csv"), langCsv.Replace("\n", "\r\n"), new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "langserver.csv"), langServerCsv.Replace("\n", "\r\n"), new UTF8Encoding(false));

            var xargs = new List<string>
            {
                $"outputCodeDir={outputDir}",
                "l10n.spaces=main,server",
                "l10n.textRefSpace=main",
                "l10n.main.tables=LanguageText",
                "l10n.main.indexMode=true",
                "l10n.main.languages=zh_CN,en_US",
                "l10n.main.outputFile=languageconfig",
                "l10n.main.outputDir=language",
                "l10n.main.keyFieldName=id",
                "l10n.main.keyFieldDesc=zh_CN",
                "l10n.server.tables=LanguageServer",
                "l10n.server.indexMode=false",
                "l10n.server.languages=zh_CN,en_US",
                "l10n.server.outputDir=languageServer",
                "l10n.server.keyFieldName=id",
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
            return dir;
        }

        /// <summary>进程内跑 default 管线（仅 cs-l10n-language 代码目标）；守卫命中时直接抛出异常。</summary>
        private static void RunCodeGen(string confDir)
        {
            var loader = new GlobalConfigLoader();
            var config = loader.Load(Path.Combine(confDir, "luban.conf"));
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
                CodeTargets = new List<string> { "cs-l10n-language" },
                DataTargets = new List<string>(),
                IncludeTags = new List<string>(),
                ExcludeTags = new List<string>(),
                Variants = new Dictionary<string, string>(),
                OutputTables = new List<string>(),
            });
        }

        private static string ReadGenerated(string confDir, string file)
        {
            // MaterializeConf 的 outputCodeDir = {confDir}/Output/code
            string path = Path.Combine(confDir, "Output", "code", file);
            return File.ReadAllText(path, Encoding.UTF8);
        }

        /// <summary>管线把代码目标异常包在 AggregateException（Task.WaitAll）里：取整条消息链（含全部内层）做断言。</summary>
        private static string RunCodeGenExpectError(string confDir)
        {
            var ex = Record.Exception(() => RunCodeGen(confDir));
            Assert.NotNull(ex);
            var messages = new List<string>();
            for (var e = (Exception)ex; e != null; e = e.InnerException)
            {
                if (e is AggregateException ae)
                {
                    foreach (var inner in ae.InnerExceptions)
                    {
                        CollectMessages(inner, messages);
                    }
                }
                else if (!messages.Contains(e.Message))
                {
                    messages.Add(e.Message);
                }
            }
            return string.Join("\n", messages);
        }

        private static void CollectMessages(Exception e, List<string> messages)
        {
            for (var cur = e; cur != null; cur = cur.InnerException)
            {
                if (!messages.Contains(cur.Message))
                {
                    messages.Add(cur.Message);
                }
            }
        }

        [Fact]
        public void 访问器单形态_name列命名_id烘焙_desc列注释()
        {
            string confDir = MaterializeConf("default", DefaultLangCsv, DefaultLangServerCsv);
            RunCodeGen(confDir);

            string main = ReadGenerated(confDir, "LanguageConfig.cs");
            Assert.Contains("public static string btn_ok => Get(10001);", main);
            Assert.Contains("public static string btn_cancel => Get(10002);", main);
            Assert.Contains("public static string mail_title => Get(10034);", main);
            Assert.Contains("public static string item_hero_name => Get(20015);", main);
            // 单形态：不再生成 Get(int) 与 dataArrRef（Get 移手写 partial）
            Assert.DoesNotContain("public static string Get(int", main);
            Assert.DoesNotContain("dataArrRef", main);
            Assert.DoesNotContain("dataMapRef[", main);
            // XML 注释取 desc 列（keyFieldDesc=zh_CN）
            Assert.Contains("/// 确定", main);
            Assert.Contains("/// 取消", main);
            Assert.Contains("/// 邮件标题", main);
            Assert.Contains("/// 英雄名字", main);
        }

        [Fact]
        public void 不生成伴生id常量_访问器保留()
        {
            // 用户验收：`public const int {name}Id = {id};` 伴生常量无消费场景，整体移除生成与 X/XId 冲突守卫；
            // 访问器 `public static string {name} => Get({id});` 仍按 keyFlag 过滤语义正常生成。
            string confDir = MaterializeConf("no-id-const", DefaultLangCsv, DefaultLangServerCsv);
            RunCodeGen(confDir);

            string main = ReadGenerated(confDir, "LanguageConfig.cs");
            Assert.Contains("public static string btn_ok => Get(10001);", main);
            Assert.Contains("public static string btn_cancel => Get(10002);", main);
            Assert.Contains("public static string mail_title => Get(10034);", main);
            // 类内不存在任何 const 常量（含伴生 id 常量与过滤形态）
            Assert.DoesNotContain("const int", main);
            Assert.DoesNotContain("btn_okId", main);
            Assert.DoesNotContain("mail_titleId", main);

            // server space（无 name 列，L_{id} 派生退化形态 L_1/L_2/L_3）同样无常量
            string server = ReadGenerated(confDir, "LanguageServerConfig.cs");
            Assert.Contains("public static string L_1 => Get(1);", server);
            Assert.DoesNotContain("const int", server);
            Assert.DoesNotContain("L_1Id", server);
        }

        [Fact]
        public void 空name的key_访问器名退化为L前缀id()
        {
            // name 列为空（is_code=true）的行：访问器名退化为 L_{id}（取代旧的裸 _{id} 形态）；
            // 有 name 的行不受影响；表整体无 name 列的 space 同样走 L_{id}。
            string langCsv = DefaultLangCsv.Replace(
                ",10002,btn_cancel,true,取消,Cancel\n",
                ",10002,btn_cancel,true,取消,Cancel\n,10005,,true,无名字段,NoName\n");
            string confDir = MaterializeConf("empty-name", langCsv, DefaultLangServerCsv);
            RunCodeGen(confDir);

            string main = ReadGenerated(confDir, "LanguageConfig.cs");
            Assert.Contains("public static string L_10005 => Get(10005);", main);
            // 不再生成裸 _{id} 形态访问器（"string _10005" 匹配访问器声明，不会误伤 L_10005）
            Assert.DoesNotContain("string _10005", main);
            Assert.Contains("public static string btn_ok => Get(10001);", main);

            // server space（表无 name 列）同样 L_{id}
            string server = ReadGenerated(confDir, "LanguageServerConfig.cs");
            Assert.Contains("public static string L_1 => Get(1);", server);
            Assert.DoesNotContain("string _1 ", server);
        }

        [Fact]
        public void server空间_形态收敛为Get且id与访问器名不变()
        {
            string confDir = MaterializeConf("server", DefaultLangCsv, DefaultLangServerCsv);
            RunCodeGen(confDir);

            string server = ReadGenerated(confDir, "LanguageServerConfig.cs");
            // server 语言表无 name 列：访问器名退化为 L_{id} 派生（L_1/L_2/L_3）；id 逐一相同
            Assert.Contains("public static string L_1 => Get(1);", server);
            Assert.Contains("public static string L_2 => Get(2);", server);
            Assert.Contains("public static string L_3 => Get(3);", server);
            // 形态收敛：不再走 dataMapRef[...] 直查（Get(int) 由手写 partial 提供）
            Assert.DoesNotContain("dataMapRef[", server);
            Assert.DoesNotContain("public static string Get(int", server);
            Assert.Contains("/// 服务器公告一", server);
            // v1 的三 space 双形态分支退役：产物里没有数组/字典分支痕迹
            Assert.DoesNotContain("dataArrRef", server);
        }

        [Fact]
        public void keyFlag行级过滤_仅标记行生成访问器()
        {
            string confDir = MaterializeConf("keyflag", DefaultLangCsv, DefaultLangServerCsv,
                "cs-l10n-language.space.main.keyFlag=is_code");
            RunCodeGen(confDir);

            string main = ReadGenerated(confDir, "LanguageConfig.cs");
            Assert.Contains("public static string btn_ok => Get(10001);", main);
            Assert.Contains("public static string btn_cancel => Get(10002);", main);
            Assert.DoesNotContain("mail_title", main);
            Assert.DoesNotContain("item_hero_name", main);
            Assert.DoesNotContain("20015", main);

            // 未配置 keyFlag 的 server space 不受影响
            string server = ReadGenerated(confDir, "LanguageServerConfig.cs");
            Assert.Contains("public static string L_1 => Get(1);", server);
            Assert.Contains("public static string L_3 => Get(3);", server);
        }

        [Fact]
        public void genAccessors为false的space_不产出访问器文件()
        {
            // AF-3/AF-16：server/aot 关闭后 cs-l10n-language 对该 space 不产出任何文件；
            // 未配置的 main 保持默认 true，访问器照常生成。
            string confDir = MaterializeConf("genaccessors", DefaultLangCsv, DefaultLangServerCsv,
                "l10n.server.genAccessors=false");
            RunCodeGen(confDir);

            Assert.True(File.Exists(Path.Combine(confDir, "Output", "code", "LanguageConfig.cs")));
            Assert.False(File.Exists(Path.Combine(confDir, "Output", "code", "LanguageServerConfig.cs")),
                "genAccessors=false 的 space 不应生成访问器文件");
            // 输出目录里只有 main 的产物
            var files = Directory.GetFiles(Path.Combine(confDir, "Output", "code"), "*.cs")
                .Select(Path.GetFileName).OrderBy(f => f).ToList();
            Assert.Equal(new[] { "LanguageConfig.cs" }, files);
        }

        [Fact]
        public void genAccessors缺省_两space都生成_向后兼容()
        {
            string confDir = MaterializeConf("genaccessors-default", DefaultLangCsv, DefaultLangServerCsv);
            RunCodeGen(confDir);

            Assert.True(File.Exists(Path.Combine(confDir, "Output", "code", "LanguageConfig.cs")));
            Assert.True(File.Exists(Path.Combine(confDir, "Output", "code", "LanguageServerConfig.cs")));
        }

        [Fact]
        public void name列重复_抛desync守卫()
        {
            string langCsv = DefaultLangCsv.Replace(",20015,item_hero_name,", ",20015,btn_ok,");
            string confDir = MaterializeConf("dupname", langCsv, DefaultLangServerCsv);
            string messages = RunCodeGenExpectError(confDir);
            Assert.Contains("name", messages);
            Assert.Contains("重复", messages);
            Assert.Contains("btn_ok", messages);
        }

        [Fact]
        public void id不在活id注册表_抛desync守卫()
        {
            // keyTable 指向 server 表：枚举出的 id（1/2/3）不在 main space 的活 id 集（10001..20015）
            string confDir = MaterializeConf("desync", DefaultLangCsv, DefaultLangServerCsv,
                "cs-l10n-language.space.main.keyTable=LanguageServer");
            string messages = RunCodeGenExpectError(confDir);
            Assert.Contains("不在活 id 注册表", messages);
            Assert.Contains("main", messages);
        }

        [Fact]
        public void 访问器名为CSharp关键字_抛错()
        {
            string langCsv = DefaultLangCsv.Replace(",10001,btn_ok,", ",10001,class,");
            string confDir = MaterializeConf("keyword", langCsv, DefaultLangServerCsv);
            string messages = RunCodeGenExpectError(confDir);
            Assert.Contains("关键字", messages);
            Assert.Contains("class", messages);
        }
    }
}
