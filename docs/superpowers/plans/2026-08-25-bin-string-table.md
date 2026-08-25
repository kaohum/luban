# 二进制导出字符串表化（bin String-Table）实现计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将 luban 二进制导出（bin/DLP1/l10n 字典/LLP2）的 string 字段改为"头部去重字符串表 + 索引引用"，普通表运行时 eager 解码、l10n 运行时懒解码，并配套升级 slg 消费方。

**Architecture:** 写端在 luban 仓库（新 `StringTableBuilder` 产出"索引区+blob"布局，`BinaryDataVisitor` 增加索引模式），读端契约 `ReadStringTable()`/`ReadStringIndex()` 由各语言运行时实现；slg 客户端代码生成 = 仓库 DLL visitor + slg 定制 cs-bin 模板（两套都要改）。

**Tech Stack:** C# (.NET 8)、Scriban 模板、xUnit（Luban.Tests）、Unity 2022.3（slg 运行时，仓库外）。

**Spec:** `docs/superpowers/specs/2026-08-25-bin-string-table-design.md`

## Global Constraints

- **格式无兼容层**（决策 A）：直接替换默认 bin 格式，不加格式标记/版本字节；旧客户端不兼容。
- **字符串表统一布局**：`[WriteSize(count)] [WriteSize(len)×count] [UTF-8 blob]`；index = 首见顺序（确定性）；空串自然入表；`null` → 空串 index。
- **记录内 string 字段**统一 `WriteSize(index)`（含列表/映射/嵌套 bean/多态 bean 内）。
- **增量行级 diff 保持内联**（`BinaryDataVisitor.Ins`）：sidecar 行 MD5 与当前行 MD5 是内容比较键，不得切索引模式。
- **bin-offset / BinaryIndexExportor 不动**（决策 B，死代码/残缺）。
- **运行时读端**：C# 由 slg 客户端包实现；Java/Go/Cpp/TS/Rust/Lua 由各自消费方按契约实现，本仓库只交付模板。
- **提交规范**：每个任务独立 commit；全部完成后按 CLAUDE.md 更新 `CHANGELOG.md` 并入最终 commit；代码风格遵循 CLAUDE.md（`Nullable disable`、4 空格缩进、Allman 大括号、MIT 头）。
- **测试串行**：涉及管线的测试必须加 `[Collection("PipelineSerial")]`（进程级单例互踩）。

---

## Phase A — 工具侧核心（luban 仓库）

### Task 1: `StringTableBuilder`（字符串表构建器）

**Files:**
- Create: `src/Luban.Core/Serialization/StringTableBuilder.cs`
- Test: `src/Luban.Tests/StringTableBuilderTests.cs`

**Interfaces:**
- Produces: `Luban.Serialization.StringTableBuilder` — `int GetOrAddIndex(string s)`（null→空串，首见顺序分配）、`int Count`、`void Write(ByteBuf buf)`（写 `[count][len×count][blob]`）。

- [ ] **Step 1: 写失败测试**

`src/Luban.Tests/StringTableBuilderTests.cs`：

```csharp
using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    public class StringTableBuilderTests
    {
        [Fact]
        public void 去重与首见顺序索引()
        {
            var b = new StringTableBuilder();
            Assert.Equal(0, b.GetOrAddIndex("a"));
            Assert.Equal(1, b.GetOrAddIndex("bb"));
            Assert.Equal(0, b.GetOrAddIndex("a")); // 去重
            Assert.Equal(2, b.GetOrAddIndex(""));  // 空串自然入表
            Assert.Equal(2, b.GetOrAddIndex(null)); // null → 空串
            Assert.Equal(3, b.Count);
        }

        [Fact]
        public void 写出布局_索引区加blob()
        {
            var b = new StringTableBuilder();
            b.GetOrAddIndex("a");
            b.GetOrAddIndex("bb");
            b.GetOrAddIndex("中文");
            var buf = new ByteBuf();
            b.Write(buf);
            // count
            Assert.Equal(3, buf.ReadSize());
            // 索引区 len（0x61='a', 0x62='b', 中文UTF8=6字节）
            Assert.Equal(1, buf.ReadSize());
            Assert.Equal(2, buf.ReadSize());
            Assert.Equal(6, buf.ReadSize());
            // blob：a bb 中文(UTF8)
            Assert.Equal((byte)'a', buf.ReadByte());
            Assert.Equal((byte)'b', buf.ReadByte());
            Assert.Equal((byte)'b', buf.ReadByte());
            byte[] zh = new byte[6];
            for (int i = 0; i < 6; i++) zh[i] = buf.ReadByte();
            Assert.Equal("中文", System.Text.Encoding.UTF8.GetString(zh));
            Assert.Equal(0, buf.Remaining);
        }

        [Fact]
        public void 空表_仅count零()
        {
            var buf = new ByteBuf();
            new StringTableBuilder().Write(buf);
            Assert.Equal(0, buf.ReadSize());
            Assert.Equal(0, buf.Remaining);
        }
    }
}
```

- [ ] **Step 2: 运行确认失败**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~StringTableBuilderTests"`
Expected: FAIL（编译错误，类型不存在）

- [ ] **Step 3: 实现**

`src/Luban.Core/Serialization/StringTableBuilder.cs`：

```csharp
// Copyright 2025 Code Philosophy
//
// [MIT 头，与仓库其它文件一致——从现有文件复制]
using System.Collections.Generic;
using System.Text;

namespace Luban.Serialization;

/// <summary>
/// 二进制字符串表构建器：收集去重字符串（首见顺序分配 index），写出统一布局
/// [WriteSize(count)] [WriteSize(len)×count] [UTF-8 blob 按序拼接]。
/// 供 bin 全量表 / DLP1 / l10n 字典 / LLP2 共用（spec 2026-08-25）。
/// </summary>
public class StringTableBuilder
{
    private readonly List<string> _strings = new List<string>();
    private readonly Dictionary<string, int> _indexMap = new Dictionary<string, int>();

    public int Count => _strings.Count;

    /// <summary>获取字符串 index（null 归一为空串），首次出现按首见顺序分配。</summary>
    public int GetOrAddIndex(string s)
    {
        if (s == null)
        {
            s = string.Empty;
        }
        if (_indexMap.TryGetValue(s, out int idx))
        {
            return idx;
        }
        idx = _strings.Count;
        _strings.Add(s);
        _indexMap.Add(s, idx);
        return idx;
    }

    /// <summary>写出字符串表到 buf。</summary>
    public void Write(ByteBuf buf)
    {
        int n = _strings.Count;
        var lengths = new int[n];
        for (int i = 0; i < n; i++)
        {
            lengths[i] = Encoding.UTF8.GetByteCount(_strings[i]);
        }
        buf.WriteSize(n);
        foreach (var len in lengths)
        {
            buf.WriteSize(len);
        }
        for (int i = 0; i < n; i++)
        {
            if (lengths[i] == 0)
            {
                continue;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(_strings[i]);
            buf.WriteBytesWithoutSize(bytes, 0, bytes.Length);
        }
    }
}
```

- [ ] **Step 4: 运行确认通过**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~StringTableBuilderTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/Serialization/StringTableBuilder.cs src/Luban.Tests/StringTableBuilderTests.cs
git commit -m "feat: 新增 StringTableBuilder——二进制字符串表(索引区+blob)构建器"
```

---

### Task 2: `BinaryDataVisitor` 索引模式 + 全量表格式

**Files:**
- Modify: `src/Luban.DataTarget.Builtin/Binary/BinaryDataVisitor.cs`
- Modify: `src/Luban.DataTarget.Builtin/Binary/BinaryDataTarget.cs`
- Test: `src/Luban.Tests/BinStringTableFormatTests.cs`（新建）

**Interfaces:**
- Consumes: `StringTableBuilder`（Task 1）。
- Produces: `BinaryDataVisitor` 新增 `public StringTableBuilder StringTable { get; set; }`（非空 → `Accept(DString)` 写 `WriteSize(index)` 并自动注册）；`BinaryDataTarget.ExportTable` 输出新全量格式。

- [ ] **Step 1: 写失败测试（管线格式断言）**

`src/Luban.Tests/BinStringTableFormatTests.cs`（复用 `ExportOnlyFieldTests` 的 mini-conf 管线模式，本测试自身带 `MaterializeAndRun` 私有辅助）：

```csharp
// Copyright 2025 Code Philosophy
//
// [MIT 头——从现有文件复制]
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
    [CollectionDefinition("PipelineSerial")]
    public class PipelineSerialCollection2 { }

    /// <summary>
    /// bin 字符串表格式测试：进程内跑 default 管线（mini conf，cs-bin + bin），
    /// 断言输出 .bytes 为 [signature][stringtable][count][records(索引引用)] 布局。
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
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-bin-st-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private string RunBinExport()
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
            conf.AppendLine("  \"targets\": [{\"name\":\"client\", \"manager\":\"Tables\", \"groups\":[\"c\"], \"topModule\":\"Table\"}],");
            conf.AppendLine("  \"xargs\": [");
            conf.AppendLine($"    \"outputCodeDir={codeDir}\",");
            conf.AppendLine($"    \"bin.outputDataDir={binDir}\"");
            conf.AppendLine("  ]");
            conf.AppendLine("}");

            var args = new List<string>
            {
                "--conf", Path.Combine(dir, "conf.json"),
                "-t", "client",
                "-c", "cs-bin",
                "-d", "bin",
            };
            File.WriteAllText(Path.Combine(dir, "conf.json"), conf.ToString(), new UTF8Encoding(false));
            var code = Program.Run(args.ToArray(), null);
            if (code != 0) throw new Exception($"pipeline failed, code={code}");
            return binDir;
        }

        [Fact]
        public void 全量表_字符串表头部_索引引用()
        {
            string binDir = RunBinExport();
            string file = Path.Combine(binDir, "LanguageText.bytes");
            Assert.True(File.Exists(file), "导出文件缺失");

            var buf = ByteBuf.Wrap(File.ReadAllBytes(file));
            string sig = buf.ReadString();
            Assert.False(string.IsNullOrEmpty(sig));

            // 字符串表
            int sc = buf.ReadSize();
            Assert.True(sc >= 5, $"字符串表应含 id外的全部字符串，实际 {sc}");
            var lens = new int[sc];
            var table = new string[sc];
            for (int i = 0; i < sc; i++) lens[i] = buf.ReadSize();
            for (int i = 0; i < sc; i++)
            {
                if (lens[i] == 0) { table[i] = ""; continue; }
                var bytes = new byte[lens[i]];
                for (int j = 0; j < lens[i]; j++) bytes[j] = buf.ReadByte();
                table[i] = Encoding.UTF8.GetString(bytes);
            }
            Assert.Contains("通用按钮", table); // 去重后只出现一次
            Assert.Contains("btn_ok", table);

            // 记录区
            int n = buf.ReadSize();
            Assert.Equal(3, n);
            // 每条: id(int) + name(index) + desc(index)
            int id1 = buf.ReadInt();
            Assert.Equal(10001, id1);
            Assert.Equal("btn_ok", table[buf.ReadSize()]);
            Assert.Equal("通用按钮", table[buf.ReadSize()]);
            Assert.Equal(0, buf.Remaining);
        }
    }
}
```

> 注：`Program.Run` 的确切签名以实现时查 `src/Luban/Program.cs` 为准（若为 `Task<int>` 则改为 `.GetAwaiter().GetResult()`）；或复用 `ExportOnlyFieldTests.MaterializeAndRun` 的既有调用方式。

- [ ] **Step 2: 运行确认失败**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~BinStringTableFormatTests"`
Expected: FAIL（记录区 string 仍是内联 `[size][bytes]`，`table[buf.ReadSize()]` 读到的是字节数而非索引）

- [ ] **Step 3: 实现 `BinaryDataVisitor` 索引模式**

`src/Luban.DataTarget.Builtin/Binary/BinaryDataVisitor.cs`：加字段 + 改 `Accept(DString)`：

```csharp
public class BinaryDataVisitor : IDataActionVisitor<ByteBuf>
{
    public static BinaryDataVisitor Ins { get; } = new();

    /// <summary>非空时开启字符串表索引模式：DString 写 WriteSize(index) 并自动注册（null 归一空串）。</summary>
    public Luban.Serialization.StringTableBuilder StringTable { get; set; }

    public void Accept(DString type, ByteBuf x)
    {
        if (StringTable != null)
        {
            x.WriteSize(StringTable.GetOrAddIndex(type.Value));
        }
        else
        {
            x.WriteString(type.Value);
        }
    }
    // ... 其余 Accept 不变
}
```

- [ ] **Step 4: 实现 `BinaryDataTarget.ExportTable` 两遍写入**

`src/Luban.DataTarget.Builtin/Binary/BinaryDataTarget.cs`：

```csharp
public override OutputFile ExportTable(DefTable table, List<Record> records)
{
    var bytes = new ByteBuf();
    // 结构签名头：客户端解表(Create)前校验，避免 .bytes 结构与代码 bean 不一致导致解表错乱
    bytes.WriteString(table.SignatureId);
    WriteStringTableAndList(table, records, bytes);
    return CreateOutputFile($"{table.OutputDataFile}.{OutputFileExt}", bytes.CopyData());
}

private void WriteStringTableAndList(DefTable table, List<Record> datas, ByteBuf x)
{
    var builder = new StringTableBuilder();
    var visitor = new BinaryDataVisitor { StringTable = builder };
    // 第一遍：注册全部字符串（写入丢弃）
    var tmp = new ByteBuf();
    foreach (var d in datas)
    {
        d.Data.Apply(visitor, tmp);
    }
    // 字符串表
    builder.Write(x);
    // 第二遍：记录（索引模式）
    x.WriteSize(datas.Count);
    foreach (var d in datas)
    {
        d.Data.Apply(visitor, x);
    }
}
```

> `BinaryDataTarget` 已 `using Luban.Serialization;`，`StringTableBuilder` 同命名空间族可直接引用；如缺 `using Luban.Serialization` 则补。

- [ ] **Step 5: 运行确认通过**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~BinStringTableFormatTests"`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/Luban.DataTarget.Builtin/Binary/BinaryDataVisitor.cs src/Luban.DataTarget.Builtin/Binary/BinaryDataTarget.cs src/Luban.Tests/BinStringTableFormatTests.cs
git commit -m "feat: bin 全量表改为头部字符串表+索引引用(BinaryDataVisitor 索引模式)"
```

---

### Task 3: DLP1 增量 patch 字符串表化

**Files:**
- Modify: `src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs`（`WritePatch`，约 216-232 行）
- Test: `src/Luban.Tests/DLP1StringTableFormatTests.cs`（新建）

**Interfaces:**
- Consumes: `StringTableBuilder`、`BinaryDataVisitor.StringTable`。
- Produces: DLP1 patch 新布局 `['D''L''P''1'][sig][字符串表][upsertCount][upsert 记录索引模式][deleteCount][delete 主键索引]*`。
- 注意：**`Handle` 中的行级 diff（sidecar 行 MD5 与当前行 MD5）保持 `BinaryDataVisitor.Ins` 内联不变**——只改 `WritePatch`。

- [ ] **Step 1: 写失败测试（管线两遍：基准 → 改数据 → 增量 → 断言 patch 布局）**

`src/Luban.Tests/DLP1StringTableFormatTests.cs`（复用 Task 2 的 mini-conf 结构与辅助，扩展出 `RunExport(dir, dataExporter)`，CSV 内容可在两遍之间改写）：

```csharp
// Copyright 2025 Code Philosophy
//
// [MIT 头——从现有文件复制]
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Luban.Serialization;
using Xunit;

namespace Luban.Tests
{
    /// <summary>
    /// DLP1 patch 字符串表格式测试：管线两遍——
    /// 1) baseline-with-sidecar 出基准；2) 改 CSV 后 incremental 出 patch。
    /// 断言 patch 为 [DLP1][sig][字符串表][upsertCount][记录索引][deleteCount][delete 索引]。
    /// </summary>
    [Collection("PipelineSerial")]
    public class DLP1StringTableFormatTests : IDisposable
    {
        private readonly string _root;

        // 与 Task 2 同构：id(int) name(string) desc(string)（zh_CN/en_US 带 group 不导出）
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
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _root = Path.Combine(Path.GetTempPath(), "luban-tests-dlp1-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_root);
        }

        public void Dispose()
        {
            try { Directory.Delete(_root, true); } catch (IOException) { }
        }

        private static string Csv(string extraRow)
        {
            return "##var,id,name,desc\n" +
                "##,语言id,访问器名,描述\n" +
                "##type,int,string,string\n" +
                ",10001,btn_ok,通用按钮\n" +
                ",10002,btn_cancel,通用按钮\n" +
                extraRow;
        }

        private string RunExport(string sub, string dataExporter, string sidecar, string csvExtraRow)
        {
            string dir = Path.Combine(_root, sub);
            Directory.CreateDirectory(Path.Combine(dir, "defines"));
            Directory.CreateDirectory(Path.Combine(dir, "Datas"));
            string codeDir = Path.Combine(dir, "Output", "code").Replace('\\', '/');
            string binDir = Path.Combine(dir, "Output", "data-bin").Replace('\\', '/');
            File.WriteAllText(Path.Combine(dir, "defines", "__tables__.xml"), SchemaXml, new UTF8Encoding(false));
            File.WriteAllText(Path.Combine(dir, "Datas", "lang.csv"), Csv(csvExtraRow).Replace("\n", "\r\n"), new UTF8Encoding(false));

            var conf = new StringBuilder();
            conf.AppendLine("{");
            conf.AppendLine("  \"groups\": [{\"names\":[\"all\",\"c\"], \"default\":true}],");
            conf.AppendLine("  \"schemaFiles\": [{\"fileName\":\"defines/__tables__.xml\", \"type\":\"\"}],");
            conf.AppendLine("  \"dataDir\": \"Datas\",");
            conf.AppendLine("  \"targets\": [{\"name\":\"client\", \"manager\":\"Tables\", \"groups\":[\"c\"], \"topModule\":\"Table\"}],");
            conf.AppendLine("  \"xargs\": [");
            conf.AppendLine($"    \"outputCodeDir={codeDir}\",");
            conf.AppendLine($"    \"bin.outputDataDir={binDir}\",");
            conf.AppendLine($"    \"dataExporter={dataExporter}\",");
            conf.AppendLine($"    \"incremental.sidecarPath={sidecar.Replace('\\\\', '/')}\",");
            conf.AppendLine($"    \"incremental.exportStamp=1700000000\"");
            conf.AppendLine("  ]");
            conf.AppendLine("}");

            var args = new List<string>
            {
                "--conf", Path.Combine(dir, "conf.json"),
                "-t", "client",
                "-c", "cs-bin",
                "-d", "bin",
            };
            File.WriteAllText(Path.Combine(dir, "conf.json"), conf.ToString(), new UTF8Encoding(false));
            var code = Program.Run(args.ToArray(), null);
            if (code != 0) throw new Exception($"{dataExporter} pipeline failed, code={code}");
            return binDir;
        }

        [Fact]
        public void DLP1_头部字符串表_upsert索引_delete主键索引()
        {
            string sidecar = Path.Combine(_root, "baseline.json");
            // 1) 基准
            RunExport("base", "baseline-with-sidecar", sidecar, "");
            // 2) 增量：改 btn_cancel 的 desc + 新增一行
            string deltaDir = RunExport("delta", "incremental", sidecar, ",10034,mail_title,邮件标题\n");

            string patch = Path.Combine(deltaDir, "LanguageText.patch.bytes");
            Assert.True(File.Exists(patch), "patch 文件缺失");

            var buf = ByteBuf.Wrap(File.ReadAllBytes(patch));
            var magic = new byte[4];
            for (int i = 0; i < 4; i++) magic[i] = buf.ReadByte();
            Assert.Equal("DLP1", Encoding.UTF8.GetString(magic));
            Assert.False(string.IsNullOrEmpty(buf.ReadString())); // signatureId

            // 字符串表
            int sc = buf.ReadSize();
            Assert.True(sc > 0);
            var lens = new int[sc];
            var table = new string[sc];
            for (int i = 0; i < sc; i++) lens[i] = buf.ReadSize();
            for (int i = 0; i < sc; i++)
            {
                if (lens[i] == 0) { table[i] = ""; continue; }
                var bytes = new byte[lens[i]];
                for (int j = 0; j < lens[i]; j++) bytes[j] = buf.ReadByte();
                table[i] = Encoding.UTF8.GetString(bytes);
            }

            // upsert 记录：id(int) + name(index) + desc(index)
            int upsertCount = buf.ReadSize();
            Assert.Equal(2, upsertCount); // 改动的 btn_cancel 行 + 新增 mail_title 行
            for (int i = 0; i < upsertCount; i++)
            {
                int id = buf.ReadInt();
                string name = table[buf.ReadSize()];
                string desc = table[buf.ReadSize()];
                Assert.False(string.IsNullOrEmpty(name));
                Assert.False(string.IsNullOrEmpty(desc));
            }

            // delete 段（本测试无删除行为 0；若实现侧 delete key 入表，断言其索引）
            int deleteCount = buf.ReadSize();
            Assert.Equal(0, deleteCount);
            Assert.Equal(0, buf.Remaining);
        }
    }
}
```

> `Program.Run` 调用签名以实现时查 `src/Luban/Program.cs` 为准（`Task<int>` 则加 `.GetAwaiter().GetResult()`）。若 `incremental` 导出器还需其它选项（以 `Handle` 源码为准补 xargs）。

- [ ] **Step 2: 运行确认失败**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~DLP1StringTableFormatTests"`
Expected: FAIL（当前 WritePatch 无字符串表，`ReadSize()` 读到的首值是 upsertCount 而非字符串表 count）

- [ ] **Step 3: 实现字符串表化**

`IncrementalDataExporter.cs` 中把 `WritePatch` 主体改为内部静态方法（供测试与自身共用）：

```csharp
internal static ByteBuf WritePatchBytes(string signatureId, List<Record> upserts, List<string> deletes)
{
    var builder = new StringTableBuilder();
    var visitor = new BinaryDataVisitor { StringTable = builder };
    // 第一遍注册：upsert 记录字符串 + delete 主键
    var tmp = new ByteBuf();
    foreach (var rec in upserts)
    {
        rec.Data.Apply(visitor, tmp);
    }
    foreach (var k in deletes)
    {
        builder.GetOrAddIndex(k);
    }

    var buf = new ByteBuf();
    PatchFormat.WriteMagic(buf, PatchFormat.MagicTable);
    buf.WriteString(signatureId);
    builder.Write(buf);
    buf.WriteSize(upserts.Count);
    foreach (var rec in upserts)
    {
        rec.Data.Apply(visitor, buf);
    }
    buf.WriteSize(deletes.Count);
    foreach (var k in deletes)
    {
        buf.WriteSize(builder.GetOrAddIndex(k));
    }
    return buf;
}

private static OutputFile WritePatch(DefTable table, string signatureId, List<Record> upserts, List<string> deletes)
{
    var buf = WritePatchBytes(signatureId, upserts, deletes);
    return new OutputFile { File = $"{table.OutputDataFile}.patch.bytes", Content = buf.CopyData() };
}
```

> 需确认 `IncrementalDataExporter.cs` 已有 `using Luban.DataExporter.Builtin.Binary;`（`BinaryDataVisitor`）与 `using Luban.Serialization;`（`ByteBuf`/`StringTableBuilder`），缺则补。**`Handle` 中的行级 diff 保持 `BinaryDataVisitor.Ins` 内联不变。**

- [ ] **Step 4: 运行确认通过**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~DLP1StringTableFormatTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs src/Luban.Tests/DLP1StringTableFormatTests.cs
git commit -m "feat: DLP1 增量 patch 头部字符串表化，upsert/delete 改索引引用"
```

---

### Task 4: l10n 字典 + LLP2 字符串表化

**Files:**
- Modify: `src/Luban.Core/Incremental/L10NChecksumUtil.cs`（`SerializeLanguageBytes`，约 163-176 行）
- Modify: `src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs`（`SerializeDictionaryToBinary`，约 128-138 行，改为薄包装）
- Modify: `src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs`（`HandleSpace` 的 LLP2 写入，约 191-201 行）
- Test: `src/Luban.Tests/LanguageIntDictSerializationTests.cs`（更新现有 3 个用例到新布局）

**Interfaces:**
- Consumes: `StringTableBuilder`。
- Produces: l10n 字典新布局 `[字符串表][pairCount][WriteKey(非string) 或 WriteSize(keyIndex)][WriteSize(valueIndex)]*`；LLP2 新布局 `['L''L''P''2'][sig][字符串表][upsertCount][WriteInt(id) WriteSize(valIndex)]*[deleteCount][WriteInt(id)]*`。
- 一致性要求：`SerializeLanguageBytes`（checksum 指纹）与 `SerializeDictionaryToBinary`（实际文件）必须逐字节一致——后者改为调用前者。

- [ ] **Step 1: 更新失败测试（现有 l10n 布局用例改为新布局）**

`src/Luban.Tests/LanguageIntDictSerializationTests.cs` 三个用例改为新布局断言（示例改第一个）：

```csharp
[Fact]
public void int字典布局_id与值()
{
    var map = new Dictionary<object, string> { [10001] = "VA", [20002] = "VB" };
    byte[] bytes = L10NChecksumUtil.SerializeLanguageBytes(map, TInt.Create(false, null));

    var buf = new ByteBuf(bytes);
    // 字符串表：VA / VB 两个值（首见顺序随字典迭代序，但 (id,value) 成对绑定断言与序无关）
    int sc = buf.ReadSize();
    Assert.Equal(2, sc);
    var lens = new int[sc];
    var table = new string[sc];
    for (int i = 0; i < sc; i++) lens[i] = buf.ReadSize();
    for (int i = 0; i < sc; i++)
    {
        if (lens[i] == 0) { table[i] = ""; continue; }
        var bytes2 = new byte[lens[i]];
        for (int j = 0; j < lens[i]; j++) bytes2[j] = buf.ReadByte();
        table[i] = Encoding.UTF8.GetString(bytes2);
    }
    Assert.Equal(2, buf.ReadSize()); // pairCount
    var got = new Dictionary<int, string>();
    for (int i = 0; i < 2; i++)
    {
        int id = buf.ReadInt();
        got[id] = table[buf.ReadSize()];
    }
    Assert.Equal("VA", got[10001]);
    Assert.Equal("VB", got[20002]);
    Assert.Equal(0, buf.Remaining);
}
```

（`null值为空串`、`空表_仅写count零` 两例相应更新：null 值 → 字符串表含空串、value 索引指向空串；空表 → `ReadSize()==0` 为字符串表 count。）

- [ ] **Step 2: 运行确认失败**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~LanguageIntDictSerializationTests"`
Expected: FAIL（新布局断言 vs 旧内联布局）

- [ ] **Step 3: 实现 `SerializeLanguageBytes`（新布局，字符串表 + 索引）**

`src/Luban.Core/Incremental/L10NChecksumUtil.cs`：

```csharp
public static byte[] SerializeLanguageBytes(Dictionary<object, string> map, TType keyType)
{
    var builder = new StringTableBuilder();
    bool stringKey = keyType is TString;
    if (stringKey)
    {
        foreach (var kv in map) builder.GetOrAddIndex((string)kv.Key);
    }
    foreach (var kv in map) builder.GetOrAddIndex(kv.Value);

    var buf = new ByteBuf();
    builder.Write(buf);
    buf.WriteSize(map.Count);
    foreach (var kv in map)
    {
        if (stringKey)
        {
            buf.WriteSize(builder.GetOrAddIndex((string)kv.Key));
        }
        else
        {
            WriteKey(buf, kv.Key, keyType);
        }
        buf.WriteSize(builder.GetOrAddIndex(kv.Value));
    }
    return buf.CopyData();
}
```

> `L10NChecksumUtil` 需 `using Luban.Serialization;`（若未引用则补）。

- [ ] **Step 4: `SerializeDictionaryToBinary` 改为薄包装**

`src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs`：

```csharp
internal static byte[] SerializeDictionaryToBinary(Dictionary<object, string> dict, TType keyType)
{
    // 与 L10NChecksumUtil.SerializeLanguageBytes 逐字节同布局（checksum 指纹口径 = 实际文件）
    return L10NChecksumUtil.SerializeLanguageBytes(dict, keyType);
}
```

（`SerializeDictionaryToBinary` 中原先的 `WriteKey` 若因此仅剩非 string 分支的本地逻辑，保留在 L10NChecksumUtil 中即可；删去本文件重复实现。）

- [ ] **Step 5: 实现 LLP2 字符串表**

`src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs` `HandleSpace` 中（约 191-201 行，magic 已由调用方写出）改：

```csharp
// 原:
// buf.WriteString(baseline.SignatureId);
// buf.WriteSize(upserts.Count);
// foreach (...) { buf.WriteInt(id); buf.WriteString(val); }
// buf.WriteSize(deletes.Count);
// foreach (...) { buf.WriteInt(id); }

// 新:
buf.WriteString(baseline.SignatureId);
var builder = new StringTableBuilder();
foreach (var upsert in upserts) builder.GetOrAddIndex(upsert.Value);
builder.Write(buf);
buf.WriteSize(upserts.Count);
foreach (var upsert in upserts)
{
    buf.WriteInt(upsert.Id);
    buf.WriteSize(builder.GetOrAddIndex(upsert.Value));
}
buf.WriteSize(deletes.Count);
foreach (var id in deletes)
{
    buf.WriteInt(id);
}
```

> 实际 upsert/delete 的容器类型与字段名以 `HandleSpace` 现有代码为准（id 为 int、val 为 string 的配对结构），照此改写。

- [ ] **Step 6: 运行确认通过**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~LanguageIntDictSerializationTests"`
Expected: PASS

- [ ] **Step 7: Commit**

```bash
git add src/Luban.Core/Incremental/L10NChecksumUtil.cs src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs src/Luban.Tests/LanguageIntDictSerializationTests.cs
git commit -m "feat: l10n 语言字典与 LLP2 patch 字符串表化（checksum 指纹同步新布局）"
```

---

## Phase B — cs-bin 代码生成（luban 仓库）

### Task 5: cs-bin string 反序列化发射 `ReadStringIndex()`

**Files:**
- Modify: `src/Luban.CSharp/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs:76`
- Test: `src/Luban.Tests/CsBinStringTableCodegenTests.cs`（新建）

**Interfaces:**
- Produces: cs-bin 生成代码中 string 字段赋值从 `_buf.ReadString()` 变为 `_buf.ReadStringIndex()`。此 visitor 同时被仓库 cs-bin 模板与 slg 定制 cs-bin 模板调用（slg 用部署 DLL）。

- [ ] **Step 1: 写失败测试（codegen 形状）**

`src/Luban.Tests/CsBinStringTableCodegenTests.cs`（复用 Task 2 的 mini-conf 管线，`-c cs-bin -d bin`，读生成代码）：

```csharp
[Fact]
public void csBin_生成代码_string为ReadStringIndex_表构造读字符串表()
{
    string codeDir = RunCodegen(); // 复用 Task 2 管线辅助，仅取 code 输出
    string tableFile = Path.Combine(codeDir, "LanguageText.cs");
    Assert.True(File.Exists(tableFile), "表类文件缺失");
    string code = File.ReadAllText(tableFile);
    Assert.Contains("ReadStringTable()", code);   // 表构造首行
    Assert.Contains("ReadStringIndex()", code);   // string 字段
    Assert.DoesNotContain("_buf.ReadString();", code);
}
```

- [ ] **Step 2: 运行确认失败**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~CsBinStringTableCodegenTests"`
Expected: FAIL（仍生成 `ReadString()`）

- [ ] **Step 3: 修改 visitor**

`src/Luban.CSharp/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs:76`：

```csharp
return $"{fieldName} = {bufName}.ReadStringIndex();";
```

- [ ] **Step 4: 运行确认通过**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~CsBinStringTableCodegenTests"`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/Luban.CSharp/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs src/Luban.Tests/CsBinStringTableCodegenTests.cs
git commit -m "feat(cs-bin): string 反序列化发射 ReadStringIndex()"
```

---

### Task 6: cs-bin 表加载入口读字符串表（仓库模板）

**Files:**
- Modify: `src/Luban.CSharp/Templates/cs-bin/table.sbn`（三个构造分支：map/list/one）

**Interfaces:**
- Consumes: Task 5 的 `ReadStringIndex()` 发射。
- Produces: 仓库 cs-bin 生成表类构造首行 `_buf.ReadStringTable();`。

- [ ] **Step 1: 修改模板（三个构造分支各加一行）**

`src/Luban.CSharp/Templates/cs-bin/table.sbn`：每个 `public {{__name}}(ByteBuf _buf)` 构造体中，在 `int n = _buf.ReadSize();` 之前加：

```
        _buf.ReadStringTable();
```

即 map 分支：

```
    public {{__name}}(ByteBuf _buf)
    {
        _buf.ReadStringTable();
        int n = _buf.ReadSize();
```

list 与 one 分支同样处理（one 分支 `if (n != 1) throw ...` 之前）。

- [ ] **Step 2: 运行确认**

Run: `cd src && dotnet test Luban.Tests --filter "FullyQualifiedName~CsBinStringTableCodegenTests"`
Expected: PASS（该测试已断言 `ReadStringTable()` 存在）

> 若测试断言表文件路径不符，按生成文件名调整（`LanguageText.cs` 或含 topModule 路径，以实际为准）。

- [ ] **Step 3: Commit**

```bash
git add src/Luban.CSharp/Templates/cs-bin/table.sbn
git commit -m "feat(cs-bin): 表构造入口先读字符串表"
```

---

## Phase C — slg 消费方（D:\work\slg，仓库外）

> 本阶段所有路径在 `D:\work\slg` 下。改完工具后**必须先重新构建并部署 Luban.dll**（`cd src && dotnet build Luban.sln`，PostBuild 自动 xcopy 到 `D:\work\slg\common\config\Tools\Luban`），模板改在 `D:\work\slg\common\config\Tools\Luban\Templates\` 就地编辑。

### Task 7: C# 运行时 `ReadStringTable` / `ReadStringIndex` / 原始字节读

**Files:**
- Modify: `D:\work\slg\client\game\Packages\com.code-philosophy.luban@1.1.2\Runtime\ByteBuf.cs`
- Modify: `D:\work\slg\client\game\Packages\com.code-philosophy.luban@1.1.2\Runtime\StreamByteBuf.cs`

**Interfaces:**
- Produces: `ByteBuf.ReadStringTable()`（virtual）、`ReadStringIndex()`、`protected string[] _stringTable`、`ReadBytesTo(byte[] dest, int offset, int count)`（virtual 原始字节读）；`StreamByteBuf` override `ReadStringTable()`（复用 mStringBuffer 零分配）与 `ReadBytesTo`。

- [ ] **Step 1: 修改 `ByteBuf.cs`**

在 `ReadString()` 附近新增：

```csharp
protected string[] _stringTable;

/// <summary>原始读取 count 字节到 dest[offset..]，不读长度前缀。子类（流式）可 override。</summary>
public virtual void ReadBytesTo(byte[] dest, int offset, int count)
{
    if (count <= 0) return;
    EnsureRead(count);
    Buffer.BlockCopy(Bytes, ReaderIndex, dest, offset, count);
    ReaderIndex += count;
}

/// <summary>
/// 解析头部字符串表（[count][len×count][UTF-8 blob]），eager 解码为 string[]。
/// 生成表类加载入口调用；随后 ReadStringIndex() 按索引取串。
/// </summary>
public virtual void ReadStringTable()
{
    int n = ReadSize();
    if (n <= 0)
    {
        _stringTable = Array.Empty<string>();
        return;
    }
    var table = new string[n];
    var tmp = new byte[256];
    for (int i = 0; i < n; i++)
    {
        int len = ReadSize();
        if (len <= 0)
        {
            table[i] = string.Empty;
            continue;
        }
        if (tmp.Length < len)
        {
            tmp = new byte[len];
        }
        ReadBytesTo(tmp, 0, len);
        table[i] = Encoding.UTF8.GetString(tmp, 0, len);
    }
    _stringTable = table;
}

/// <summary>按索引取字符串表条目（记录内 string 字段反序列化用）。</summary>
public string ReadStringIndex()
{
    int idx = ReadSize();
    if (_stringTable == null || (uint)idx >= (uint)_stringTable.Length)
    {
        throw new SerializationException("ReadStringIndex out of range: " + idx);
    }
    return _stringTable[idx];
}
```

- [ ] **Step 2: 修改 `StreamByteBuf.cs`**

新增两个 override：

```csharp
public override void ReadBytesTo(byte[] dest, int offset, int count)
{
    ReadBytesRaw(dest, offset, count);
}

public override void ReadStringTable()
{
    int n = ReadSize();
    if (n <= 0)
    {
        _stringTable = Array.Empty<string>();
        return;
    }
    var table = new string[n];
    for (int i = 0; i < n; i++)
    {
        int len = ReadSize();
        if (len <= 0)
        {
            table[i] = string.Empty;
            continue;
        }
        if (mStringBuffer == null || mStringBuffer.Length < len)
        {
            mStringBuffer = new byte[len];
        }
        ReadBytesRaw(mStringBuffer, 0, len);
        table[i] = Encoding.UTF8.GetString(mStringBuffer, 0, len);
    }
    _stringTable = table;
}
```

- [ ] **Step 3: 编译验证**

在 Unity 编辑器打开项目（或 `! "D:\work\slg\client\game\Unity.exe" -batchmode -quit -projectPath ...` 由用户执行），确认编译通过。无头验证可用：检查 `ByteBuf.cs` 语法一致性（无 IDE 时人工审查 + Phase C 后续任务实测）。
Expected: 编译零错误

- [ ] **Step 4: Commit（slg 仓库）**

```bash
cd D:/work/slg && git add client/game/Packages/com.code-philosophy.luban@1.1.2/Runtime/ByteBuf.cs client/game/Packages/com.code-philosophy.luban@1.1.2/Runtime/StreamByteBuf.cs
git commit -m "feat(runtime): ByteBuf/StreamByteBuf 支持字符串表读取(ReadStringTable/ReadStringIndex)"
```

---

### Task 8: 手写 l10n 读取端懒解码（LanguageConfig）

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Scripts\Configs\LanguageConfig.cs`（构造 + `ApplyDelta`）
- 参照: `D:\work\slg\client\game\Assets\Scripts\Configs\ConfigTableInitializer.cs`（加载入口，一般无需改）

**Interfaces:**
- Consumes: `StreamByteBuf.ReadBytesTo` / `ReadSize` / `ReadInt`（Task 7）。
- Produces: 懒解码 `LanguageConfig`——加载只解析索引区 + blob 常驻，`Get(id)` 首次解码缓存；`ApplyDelta` 适配 LLP2 新布局。

- [ ] **Step 1: 重写构造为懒解码**

`LanguageConfig.cs` 构造（原 `int n = buf.ReadSize(); ... ReadString()` 循环）改为：

```csharp
public LanguageConfig(ByteBuf buf)
{
    // v2 字符串表：懒解码。加载只解析索引区，blob 常驻，Get 首次访问才解码。
    int sc = buf.ReadSize();
    var lens = new int[sc];
    int blobSize = 0;
    for (int i = 0; i < sc; i++)
    {
        lens[i] = buf.ReadSize();
        blobSize += lens[i];
    }
    var blob = new byte[blobSize];
    buf.ReadBytesTo(blob, 0, blobSize);

    int n = buf.ReadSize();
    _idToIndex = PooledHashMap<int, int>.Create(n);
    for (int i = 0; i < n; i++)
    {
        int id = buf.ReadInt();
        _idToIndex[id] = buf.ReadSize(); // value 字符串表索引
    }
    _blob = blob;
    _lens = lens;
    _cache = new string[sc];
}

public string Get(int id)
{
    if (!_idToIndex.TryGetValue(id, out int idx))
    {
        return string.Empty;
    }
    string s = _cache[idx];
    if (s == null)
    {
        int len = _lens[idx];
        if (len <= 0)
        {
            s = string.Empty;
        }
        else
        {
            int off = 0;
            for (int i = 0; i < idx; i++) off += _lens[i];
            s = Encoding.UTF8.GetString(_blob, off, len);
        }
        _cache[idx] = s;
    }
    return s;
}
```

> 新增私有字段：`byte[] _blob; int[] _lens; string[] _cache; PooledHashMap<int,int> _idToIndex;`（原 `map` 字段迁移）。偏移量可改为加载时预累计 `_offsets[]` 避免每次累加——实现时若 `Get` 热点，建 `_offsets` 数组。

- [ ] **Step 2: 重写 `ApplyDelta`（LLP2 新布局）**

`ApplyDelta`（原 `['L''L''P''2'][sig][upsertCount][id,val]*[deleteCount][id]*`）改为：

```csharp
public void ApplyDelta(ByteBuf buf)
{
    if (buf.ReadByte() != 'L' || buf.ReadByte() != 'L' || buf.ReadByte() != 'P' || buf.ReadByte() != '2')
    {
        throw new SerializationException("expect LLP2 magic");
    }
    buf.ReadString(); // signatureId(外部已校验,跳过)

    // 增量 patch 自带字符串表：把新文本并入本实例的 blob/索引（追加式）
    int sc = buf.ReadSize();
    var patchLens = new int[sc];
    int patchBlobSize = 0;
    for (int i = 0; i < sc; i++) { patchLens[i] = buf.ReadSize(); patchBlobSize += patchLens[i]; }
    var patchBlob = new byte[patchBlobSize];
    buf.ReadBytesTo(patchBlob, 0, patchBlobSize);

    // 合并到本实例
    int baseCount = _lens.Length;
    var mergedLens = new int[baseCount + sc];
    Array.Copy(_lens, mergedLens, baseCount);
    Array.Copy(patchLens, 0, mergedLens, baseCount, sc);
    var mergedBlob = new byte[_blob.Length + patchBlob.Length];
    Array.Copy(_blob, mergedBlob, _blob.Length);
    Array.Copy(patchBlob, 0, mergedBlob, _blob.Length, patchBlob.Length);
    _lens = mergedLens;
    _blob = mergedBlob;
    var mergedCache = new string[_cache.Length + sc];
    Array.Copy(_cache, mergedCache, _cache.Length);
    _cache = mergedCache;

    int upsertCount = buf.ReadSize();
    for (int i = 0; i < upsertCount; i++)
    {
        int id = buf.ReadInt();
        _idToIndex[id] = baseCount + buf.ReadSize(); // patch 内相对索引 → 合并后绝对索引
    }
    int deleteCount = buf.ReadSize();
    for (int i = 0; i < deleteCount; i++)
    {
        _idToIndex.Remove(buf.ReadInt());
    }
}
```

- [ ] **Step 3: 验证**

运行 Phase C Task 10 的全量+增量导出，客户端进游戏验证文本显示正确（多语言切换、增量热更后文本一致）。
Expected: 文本加载正确、`Get` 懒解码生效（未访问文本无 string 对象——可用内存快照/日志确认）

- [ ] **Step 4: Commit（slg 仓库）**

```bash
cd D:/work/slg && git add client/game/Assets/Scripts/Configs/LanguageConfig.cs
git commit -m "feat(config): LanguageConfig 懒解码读取端(blob常驻,Get首访解码)+LLP2合并"
```

---

### Task 9: slg 定制 cs-bin 模板（Create/Append/MergeApply）

**Files:**
- Modify: `D:\work\slg\common\config\Tools\Luban\Templates\cs-bin\table.sbn`

**Interfaces:**
- Consumes: 部署 DLL 的 `BinaryUnderlyingDeserializeVisitor`（Task 5，string 自动 `ReadStringIndex()`）；运行时 `ReadStringTable()`（Task 7）。
- Produces: 重新生成的表类在 `Create`/`Append`/`MergeApply` 正确读字符串表。

- [ ] **Step 1: 修改模板**

`D:\work\slg\common\config\Tools\Luban\Templates\cs-bin\table.sbn`：
1. 每个 `Create(ByteBuf ...)`（map 110 行、list 234 行、one 412 行附近）：在签名校验后、`int n = ...ReadSize();` 前插入：

```
        buf.ReadStringTable();
```

2. 每个 `Append`（125、272 行附近）：同样在签名校验后插入 `buf.ReadStringTable();`。

3. 每个 `MergeApply`（149、315、380 行附近）：在 magic 校验 + `buf.ReadString();`（signature 跳过）之后、`int upsertCount = buf.ReadSize();` 之前插入 `buf.ReadStringTable();`。

4. delete 主键读取：`string keyStr = buf.ReadString();`（181、347 行）改为 `string keyStr = buf.ReadStringIndex();`。

> 注意：模板中两个 `MergeApply` 分支（list 表 315 行主分支与 380 行回退分支）都要改；380 行回退变体 `Create(_buf)` 由 Create 内已读字符串表覆盖，无需额外处理。

- [ ] **Step 2: 构建并部署新 DLL + 重新生成代码**

```bash
cd E:/Projects/luban/src && dotnet build Luban.sln   # PostBuild 自动部署 Luban.dll 到 slg Tools/Luban
```

然后重新执行基准导出（Phase C Task 10 的步骤），检查生成代码：
- `D:\work\slg\client\game\Assets\Scripts\Configs\Gen\AccessConfig.cs` 等：`Create`/`Append` 含 `buf.ReadStringTable();`，`MergeApply` 含字符串表读取，string 字段赋值为 `ReadStringIndex()`。
Expected: 生成代码符合新格式

- [ ] **Step 3: Commit（slg 仓库）**

```bash
cd D:/work/slg && git add common/config/Tools/Luban/Templates/cs-bin/table.sbn
git commit -m "feat(templates): 定制 cs-bin 模板接入字符串表(Create/Append/MergeApply)"
```

---

### Task 10: 全量重新基准 + 端到端验证

**Files:**
- 验证产物: `D:\work\slg\common\config\Output\`（重新导出 + 重新基准）

- [ ] **Step 1: 重新基准导出**

```bash
cd D:/work/slg/common/config
# 先清理旧基准（行字节表示变化，旧 baseline 不可用）
rm -rf Output/baseline
# 重跑基准导出（bat 会重建 baseline）
.\1基准配置导出.bat
```

Expected: 导出成功、无错误；`Output/baseline/tables.json` 等重建。

- [ ] **Step 2: 客户端加载验证**

Unity 打开 `D:\work\slg\client\game`，运行进游戏：
- 普通表（如 Access/Activity 等）正常加载，DataList/DataMap 与旧格式数据一致
- 多语言文本正确显示（zh_CN/en_US 切换）
- 服务器 JSON 配置无变化（`java-json` 不受影响，抽查一份 json 与改造前一致）

Expected: 全部正常

- [ ] **Step 3: 增量验证（DLP1 + LLP2）**

改一行表数据 + 一条语言文本 → 跑 `.\2增量配置导出.bat` → 客户端应用增量（DLP1 `MergeApply` + LLP2 `ApplyDelta`）：
- 修改的行正确热更、未修改行不受影响
- 语言文本新增/修改/删除正确应用
Expected: 增量热更正常

- [ ] **Step 4: 内存验证（懒解码生效）**

Profiler/内存快照确认：l10n 加载后仅 blob 常驻 + 已访问文本的 string；未访问文本无 string 对象；普通表无原始字节驻留。

- [ ] **Step 5: 收尾 commit（含 CHANGELOG）**

在 luban 仓库更新 `CHANGELOG.md`（新增当日段落，记录字符串表化改动、影响面、向后兼容说明），与最终代码改动一并提交（若工具侧已在各任务 commit，则此处单独补 changelog commit）。

---

## Phase D — 其它语言 bin 模板（luban 仓库）

> 各语言运行时在各自消费方（按设计 5.3 契约实现 `readStringTable()`/`readStringIndex()`）。本阶段只改模板/发射逻辑，验证 = codegen 输出形状检查（`Luban.Tests` 的 shape 断言），运行时就绪性由消费方负责。每语言一个 Task，模式相同。

### Task 11: java-bin

**Files:**
- Modify: `src/Luban.Java/TypeVisitors/JavaBinUnderlyingDeserializeVisitor.cs:75`（`readString()` → `readStringIndex()`）
- Modify: `src/Luban.Java/Templates/java-bin/table.sbn`（每个 `public {{__name}}(ByteBuf _buf)` 构造体 `for(int n = _buf.readSize()...` 之前加 `_buf.readStringTable();`，map/list/one 三处）

- [ ] **Step 1: 修改 visitor 与模板**

```csharp
// JavaBinUnderlyingDeserializeVisitor.cs:75
return $"{fieldName} = {bufName}.readStringIndex();";
```

```sbn
    public {{__name}}(ByteBuf _buf) {
        _buf.readStringTable();
        // ...原有代码
```

- [ ] **Step 2: 验证（codegen shape）**

在 `src/Luban.Tests` 增加 `JavaBinStringTableCodegenTests`（复用 Task 2 管线辅助，`-c java-bin -d bin`），断言生成 java 表类含 `readStringTable()` 与 `readStringIndex()`。

- [ ] **Step 3: Commit**

```bash
git add src/Luban.Java/TypeVisitors/JavaBinUnderlyingDeserializeVisitor.cs src/Luban.Java/Templates/java-bin/table.sbn src/Luban.Tests/JavaBinStringTableCodegenTests.cs
git commit -m "feat(java-bin): 字符串表化——readStringIndex + 表构造读字符串表"
```

### Task 12: go-bin

**Files:**
- Modify: `src/Luban.Golang/TypeVisitors/BinUnderlyingDeserializeVisitor.cs:73`（`ReadString()` → `ReadStringIndex()`）
- Modify: `src/Luban.Golang/Templates/go-bin/table.sbn`（`New{{go_full_name}}` 内 `if size, err := _buf.ReadSize()` 之前加 `if _, err := _buf.ReadStringTable(); err != nil { return nil, err }`，map/list/one 三处）

- [ ] **Step 1: 修改 visitor 与模板**

```csharp
// BinUnderlyingDeserializeVisitor.cs:73
return $"{{ if {fieldName}, {err} = {bufName}.ReadStringIndex(); {err} != nil {{ {err} = errors.New(\"error\"); return }} }}";
```

```sbn
	if _, err := _buf.ReadStringTable() ; err != nil {
		return nil, err
	}
	if size, err := _buf.ReadSize() ; err != nil {
		return nil, err
	} else {
```

- [ ] **Step 2: 验证 + Commit**

模式同 Task 11（`GoBinStringTableCodegenTests` 断言 `ReadStringIndex` / `ReadStringTable`）。

```bash
git add src/Luban.Golang/TypeVisitors/BinUnderlyingDeserializeVisitor.cs src/Luban.Golang/Templates/go-bin/table.sbn src/Luban.Tests/GoBinStringTableCodegenTests.cs
git commit -m "feat(go-bin): 字符串表化"
```

### Task 13: cpp-rawptr-bin + cpp-sharedptr-bin

**Files:**
- Modify: `src/Luban.Cpp/TypeVisitors/CppUnderlyingDeserializeVisitorBase.cs:71`（`readString({fieldName})` → `readStringIndex({fieldName})`）
- Modify: `src/Luban.Cpp/Templates/cpp-rawptr-bin/table.sbn` 与 `src/Luban.Cpp/Templates/cpp-sharedptr-bin/table.sbn`（`bool load(::luban::ByteBuf& _buf)` 中 `int n; if (!_buf.readSize(n)) return false;` 之前加 `if (!_buf.readStringTable()) return false;`，各模式分支同）

- [ ] **Step 1: 修改 visitor 与模板**

```csharp
// CppUnderlyingDeserializeVisitorBase.cs:71
return $"if(!{bufName}.readStringIndex({fieldName})) return false;";
```

```sbn
        if (!_buf.readStringTable()) return false;
        int n;
        if (!_buf.readSize(n)) return false;
```

- [ ] **Step 2: 验证 + Commit**

`CppBinStringTableCodegenTests` 断言 `readStringTable` / `readStringIndex`。

```bash
git add src/Luban.Cpp/TypeVisitors/CppUnderlyingDeserializeVisitorBase.cs src/Luban.Cpp/Templates/cpp-rawptr-bin/table.sbn src/Luban.Cpp/Templates/cpp-sharedptr-bin/table.sbn src/Luban.Tests/CppBinStringTableCodegenTests.cs
git commit -m "feat(cpp-bin): 字符串表化"
```

### Task 14: typescript-bin

**Files:**
- Modify: `src/Luban.Typescript/TypeVisitors/BinUnderingDeserializeVisitorBase.cs:72`（`readString()` → `readStringIndex()`）
- Modify: `src/Luban.Typescript/Templates/typescript-bin/schema.sbn`（表类 `constructor(_buf_: ByteBuf)` 各分支 `_buf_.readSize()` 之前加 `_buf_.readStringTable()`；bean 构造不变）

- [ ] **Step 1: 修改 visitor 与模板**

```csharp
// BinUnderingDeserializeVisitorBase.cs:72
return $"{fieldName} = {bufName}.readStringIndex()";
```

```sbn
    constructor(_buf_: ByteBuf) {
        _buf_.readStringTable()
        // ...原有
```

- [ ] **Step 2: 验证 + Commit**

`TypescriptBinStringTableCodegenTests` 断言。

```bash
git add src/Luban.Typescript/TypeVisitors/BinUnderingDeserializeVisitorBase.cs src/Luban.Typescript/Templates/typescript-bin/schema.sbn src/Luban.Tests/TypescriptBinStringTableCodegenTests.cs
git commit -m "feat(typescript-bin): 字符串表化"
```

### Task 15: rust-bin

**Files:**
- Modify: `src/Luban.Rust/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs:76`（`read_string()` → `read_string_index()`）
- Modify: `src/Luban.Rust/Templates/rust-bin/mod.sbn`（各 `pub fn new(mut buf: ByteBuf/&mut ByteBuf)` 读 size 之前加 `buf.read_string_table()?;`——以现有错误处理风格 `?`/`LubanError` 为准）

- [ ] **Step 1: 修改 visitor 与模板**

```csharp
// Luban.Rust/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs:76
return $"{bufName}.read_string_index()";
```

```sbn
    pub fn new(mut buf: ByteBuf) -> Result<...> {
        buf.read_string_table()?;
        // ...原有
```

- [ ] **Step 2: 验证 + Commit**

`RustBinStringTableCodegenTests` 断言。

```bash
git add src/Luban.Rust/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs src/Luban.Rust/Templates/rust-bin/mod.sbn src/Luban.Tests/RustBinStringTableCodegenTests.cs
git commit -m "feat(rust-bin): 字符串表化"
```

### Task 16: lua-bin

**Files:**
- Modify: `src/Luban.Lua/Templates/lua-bin/schema.sbn`（`InitTypes` 注册 `readStringTable`/`readStringIndex` 方法并替换 string 读取；表加载入口调用 `readStringTable`）

- [ ] **Step 1: 修改模板**

`schema.sbn` 的 `InitTypes` 中：

```lua
    local readString = methods.readString
    local readStringIndex = methods.readStringIndex
    local readStringTable = methods.readStringTable
```

string 类型读取处（`readString(bs)` 出现点）改为 `readStringIndex(bs)`；表加载处（读 record count 前）调用 `readStringTable(bs)`。

- [ ] **Step 2: 验证 + Commit**

`LuaBinStringTableCodegenTests` 断言 schema 输出含 `readStringTable` / `readStringIndex`。

```bash
git add src/Luban.Lua/Templates/lua-bin/schema.sbn src/Luban.Tests/LuaBinStringTableCodegenTests.cs
git commit -m "feat(lua-bin): 字符串表化"
```

---

## 自审记录

**Spec 覆盖核对：**
- 全量表格式（spec 4.2）→ Task 2 ✓
- DLP1（4.3）→ Task 3 ✓
- l10n 字典 + LLP2（4.4）→ Task 4 ✓
- 运行时策略 eager（5.1）→ Task 7 ✓；懒解码（5.2）→ Task 8 ✓
- 其它语言契约（5.3）→ Task 11-16（模板），运行时契约交付 ✓
- 工具侧实现表（spec §6）→ Task 1-4（含 diff 保持内联、bin-offset/死代码不动）✓
- 模板改动（spec §7.1 仓库、7.2 slg 定制）→ Task 5-6 + 9、11-16 ✓
- slg 升级清单（spec §8）→ Task 7-10 ✓
- 迁移（重新基准、checksum 表随 ExportTable 自动适配）→ Task 10 ✓
- 验收标准（spec §10）→ Task 10 端到端验证 ✓

**已知未覆盖 / 实现时需确认（均为低风险细节）：**
1. `Program.Run` 的调用签名（`int` 还是 `Task<int>`）以 `src/Luban/Program.cs` 实际为准——各管线测试统一处理。
2. LLP2 的 upsert/delete 容器字段名以 `IncrementalL10NDataExporter.HandleSpace` 现有代码为准。
3. 各语言 codegen shape 测试的生成文件名/断言串以实际生成输出为准。
4. `incremental` 导出器是否还需 `exportStamp` 之外的必要选项，以 `Handle` 源码为准（测试已传固定 stamp）。
