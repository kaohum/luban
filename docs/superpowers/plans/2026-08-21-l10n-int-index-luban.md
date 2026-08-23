# 多语言 int 下标改造 — Plan 1:Luban 工具侧 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 Luban 中实现 text 字段→int 下标转换、append-only 下标注册表(sidecar v2)、数组格式语言 bin、LLP2 增量 patch、omnibus 组合导出器、cs-l10n-language 多 space 代码生成。

**Architecture:** 复用现有 `text` 类型语法糖作为语言 key 锚点;数据加载完成后(进 checksum 之前)把 text 标签的 DString 改写为 DInt(注册表下标);语言表按注册表顺序导出纯字符串数组;增量以 LLP2 全下标 patch 表达,相对基准累计 diff;omnibus 导出器按 space 名单路由普通表/语言表。

**Tech Stack:** .NET 8 / C#(Luban 源码)、xunit(新增最小测试工程)、Scriban 模板。

**Spec:** `docs/superpowers/specs/2026-08-21-l10n-int-index-merged-pipeline-design.md`(本计划实现其 §3、§4;§5~§7 配置仓与客户端在 Plan 2)

## Global Constraints

- 遵守仓库 C# 约定(CLAUDE.md):PascalCase 类/方法,`_camelCase` 私有字段,`Nullable disable`(**不用**可空引用类型),NLog `s_logger`,MIT 文件头,4 空格缩进,传统 `namespace { }` 块。
- 构建:`cd src && dotnet build Luban.sln`。构建成功后 PostBuild 自动把 DLL 部署到 slg 配置仓(不要手动拷贝)。
- 仓库原本无自动化测试;本计划新增 `Luban.Tests`(xunit)仅覆盖纯逻辑(分配器/转换层/格式 writer),其余走 mini conf 流水验证。
- 未配置 `l10n.spaces` 时,现有单值 l10n 选项与 `l10n-bin-split`/`l10n-baseline-with-sidecar`/`incremental-l10n-bin-split` 导出器行为必须**完全不变**(对 slg 之外用户零破坏)。
- 关键不变量:下标分配幂等;增量 run 必须回写 sidecar KeyEntries;转换层必须发生在 `CalculateTableChecksums()` **之前**。
- 每个 Task 完成即 commit(遵循仓库 changelog 规约:最终验收后统一补 CHANGELOG,随代码同 commit;各任务先正常 commit 代码)。

## 关键跨任务接口(先读这里)

```csharp
// Task 1 产出,后续全依赖
namespace Luban.Incremental;
public class KeyEntry { public string Key { get; set; } public bool Deleted { get; set; } }
public static class KeyIndexAllocator
{
    // 幂等分配:prev 位置=旧下标;新 key Ordinal 排序追加尾部;curKeys 中消失的旧 key 打墓碑
    public static List<KeyEntry> Allocate(IReadOnlyList<KeyEntry> prev, IEnumerable<string> currentKeys);
    public static IReadOnlyList<KeyEntry> LoadPrev(string sidecarPath); // 文件不存在/旧格式 → 空表
}

// Task 3 产出
namespace Luban.L10N;
public class L10NSpace
{
    public string Name; public List<string> Tables; public bool IndexMode;
    public List<string> Languages; public string OutputFile; public string OutputDir;
    public string KeyFieldName; public string KeyFieldDesc; public string SidecarPath;
    public L10NKeyIndex KeyIndex; // Task 4 填充
}

// Task 4 产出
namespace Luban.L10N;
public class L10NKeyIndex
{
    public List<KeyEntry> Entries { get; }          // 分配后的注册表
    public bool TryGetIndex(string key, out int index); // 墓碑/不存在 → false
    public int Count;
}
```

`GenerationContext` 新增(现有成员不动):`public IReadOnlyList<L10NSpace> L10NSpaces { get; private set; }`、`public bool L10NTextIndexEnabled { get; private set; }`(任一 space IndexMode=true 即 true,且配置了 spaces)。

---

### Task 0: 测试工程脚手架

**Files:**
- Create: `src/Luban.Tests/Luban.Tests.csproj`
- Create: `src/Luban.Tests/KeyIndexAllocatorTests.cs`(空占位,Task 1 填)
- Modify: `src/Luban.sln`(添加工程)

**Interfaces:** Produces: 可运行的 `dotnet test src/Luban.Tests`。

- [ ] **Step 1: 创建测试工程**

`src/Luban.Tests/Luban.Tests.csproj`:

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>disable</Nullable>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="17.9.0" />
    <PackageReference Include="xunit" Version="2.7.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="2.5.7" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\Luban.Core\Luban.Core.csproj" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 挂进解决方案并验证空跑**

```bash
cd src && dotnet sln Luban.sln add Luban.Tests/Luban.Tests.csproj && dotnet test Luban.Tests
```

Expected: build 成功,0 tests(占位文件为空类)。

- [ ] **Step 3: Commit**

```bash
git add src/Luban.Tests src/Luban.sln
git commit -m "test: 新增 Luban.Tests xunit 测试工程"
```

---

### Task 1: KeyEntry 模型 + KeyIndexAllocator(TDD)

**Files:**
- Modify: `src/Luban.Core/Incremental/SidecarModels.cs`(加 `KeyEntry`)
- Create: `src/Luban.Core/Incremental/KeyIndexAllocator.cs`
- Test: `src/Luban.Tests/KeyIndexAllocatorTests.cs`

**Interfaces:**
- Consumes: `L10NSidecar`(已存在)
- Produces: `KeyEntry`、`KeyIndexAllocator.Allocate(...)`、`KeyIndexAllocator.LoadPrev(sidecarPath)`(签名见"关键跨任务接口")

- [ ] **Step 1: 写失败测试**

`src/Luban.Tests/KeyIndexAllocatorTests.cs`:

```csharp
using Luban.Incremental;
using Xunit;

public class KeyIndexAllocatorTests
{
    private static KeyEntry E(string key, bool deleted = false) => new() { Key = key, Deleted = deleted };

    [Fact]
    public void 首次分配_按Ordinal排序()
    {
        var entries = KeyIndexAllocator.Allocate(null, new[] { "b_key", "a_key", "c_key" });
        Assert.Equal(new[] { "a_key", "b_key", "c_key" }, entries.Select(e => e.Key));
        Assert.All(entries, e => Assert.False(e.Deleted));
    }

    [Fact]
    public void 重复分配_幂等()
    {
        string[] keys = { "a", "c", "e" };
        var first = KeyIndexAllocator.Allocate(null, keys);
        var second = KeyIndexAllocator.Allocate(first, keys);
        Assert.Equal(first.Select(e => (e.Key, e.Deleted)), second.Select(e => (e.Key, e.Deleted)));
    }

    [Fact]
    public void 新key追加尾部_旧下标不变()
    {
        var prev = KeyIndexAllocator.Allocate(null, new[] { "a", "b" });   // a=0 b=1
        var next = KeyIndexAllocator.Allocate(prev, new[] { "a", "b", "z", "c" }); // c,z 追加
        Assert.Equal("a", next[0].Key);
        Assert.Equal("b", next[1].Key);
        Assert.Equal(new[] { "c", "z" }, new[] { next[2].Key, next[3].Key }); // Ordinal 排序后追加
    }

    [Fact]
    public void 删除key打墓碑_下标不回收()
    {
        var prev = KeyIndexAllocator.Allocate(null, new[] { "a", "b", "c" }); // b=1
        var next = KeyIndexAllocator.Allocate(prev, new[] { "a", "c" });
        Assert.Equal(3, next.Count);
        Assert.True(next[1].Deleted);
        Assert.Equal("b", next[1].Key); // 墓碑保留原 key
        // 墓碑后追加的新 key 不会复用 1
        var next2 = KeyIndexAllocator.Allocate(next, new[] { "a", "c", "d" });
        Assert.Equal("d", next2[3].Key);
    }

    [Fact]
    public void 墓碑复活_原位复活()
    {
        var prev = KeyIndexAllocator.Allocate(null, new[] { "a", "b", "c" });
        var deleted = KeyIndexAllocator.Allocate(prev, new[] { "a", "c" });
        var revived = KeyIndexAllocator.Allocate(deleted, new[] { "a", "b", "c" });
        Assert.False(revived[1].Deleted);
        Assert.Equal("b", revived[1].Key);
    }

    [Fact]
    public void 两次增量交错追加_下标不漂移()
    {
        // 增量1 加 z1;增量2 加 z0(Ordinal 靠前)——z1 下标必须保持
        var base0 = KeyIndexAllocator.Allocate(null, new[] { "a" });
        var inc1 = KeyIndexAllocator.Allocate(base0, new[] { "a", "z1" }); // z1=1
        var inc2 = KeyIndexAllocator.Allocate(inc1, new[] { "a", "z1", "z0" }); // z0 追加=2,z1 仍=1
        Assert.Equal("z1", inc2[1].Key);
        Assert.Equal("z0", inc2[2].Key);
    }

    [Fact]
    public void LoadPrev_文件不存在_返回空()
    {
        Assert.Empty(KeyIndexAllocator.LoadPrev("Z:/not/exist/l10n.json"));
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd src && dotnet test Luban.Tests`
Expected: 编译失败(`KeyEntry`/`KeyIndexAllocator` 不存在)。

- [ ] **Step 3: 实现**

`SidecarModels.cs` 在 `L10NSidecar` 类外追加:

```csharp
/// <summary>
/// 下标注册表条目:所在列表的位置 == 全局下标。Deleted=true 为墓碑(槽位保留,不回收)。
/// </summary>
public class KeyEntry
{
    public string Key { get; set; } = "";

    public bool Deleted { get; set; }
}
```

`L10NSidecar` 增加属性(放在 `Keys` 之前,带注释):

```csharp
/// <summary>
/// living 注册表:位置 == 全局下标,墓碑占位。基准与增量运行都会回写。
/// 旧格式 sidecar(只有 Keys)读入时视为空注册表重新分配。
/// </summary>
public List<KeyEntry> KeyEntries { get; set; } = new();
```

`src/Luban.Core/Incremental/KeyIndexAllocator.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Luban.Incremental;

/// <summary>
/// 语言 key 下标注册表分配器(纯函数,幂等核心)。
/// 规则:旧 key 保持下标;新 key 按 Ordinal 排序追加尾部;消失的 key 原位打墓碑,永不回收。
/// 同一 (prev, currentKeys) 输入恒定产出同一注册表 → 跨次导表下标稳定。
/// </summary>
public static class KeyIndexAllocator
{
    public static List<KeyEntry> Allocate(IReadOnlyList<KeyEntry> prev, IEnumerable<string> currentKeys)
    {
        var curSet = new HashSet<string>(currentKeys.Where(k => !string.IsNullOrEmpty(k)), StringComparer.Ordinal);
        var result = new List<KeyEntry>(prev?.Count ?? curSet.Count);
        var live = new HashSet<string>(StringComparer.Ordinal);

        if (prev != null)
        {
            foreach (var e in prev)
            {
                if (curSet.Contains(e.Key))
                {
                    result.Add(new KeyEntry { Key = e.Key, Deleted = false }); // 复活或保持
                    live.Add(e.Key);
                }
                else
                {
                    result.Add(new KeyEntry { Key = e.Key, Deleted = true });  // 墓碑
                }
            }
        }

        foreach (var k in curSet.Where(k => !live.Contains(k)).OrderBy(k => k, StringComparer.Ordinal))
        {
            result.Add(new KeyEntry { Key = k, Deleted = false });
        }
        return result;
    }

    /// <summary>
    /// 读上次 sidecar 的 KeyEntries;文件不存在或旧格式(无 KeyEntries)返回空表。
    /// </summary>
    public static IReadOnlyList<KeyEntry> LoadPrev(string sidecarPath)
    {
        if (string.IsNullOrEmpty(sidecarPath) || !File.Exists(sidecarPath))
        {
            return Array.Empty<KeyEntry>();
        }
        try
        {
            var sidecar = BaselineSidecarIO.LoadL10N(sidecarPath);
            return sidecar?.KeyEntries ?? (IReadOnlyList<KeyEntry>)Array.Empty<KeyEntry>();
        }
        catch (Exception)
        {
            return Array.Empty<KeyEntry>();
        }
    }
}
```

- [ ] **Step 4: 跑测试通过**

Run: `cd src && dotnet test Luban.Tests`
Expected: 全部 PASS。

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/Incremental/SidecarModels.cs src/Luban.Core/Incremental/KeyIndexAllocator.cs src/Luban.Tests/KeyIndexAllocatorTests.cs
git commit -m "feat: L10N 下标注册表 KeyEntry 模型与幂等分配器 KeyIndexAllocator"
```

---

### Task 2: sidecar v2 读写(基准/增量统一回写)

**Files:**
- Modify: `src/Luban.Core/Incremental/BaselineSidecarIO.cs`
- Test: `src/Luban.Tests/BaselineSidecarIOTests.cs`

**Interfaces:**
- Consumes: `KeyEntry`(Task 1)
- Produces: `L10NSidecar.KeyEntries` 的 JSON 持久化往返(现有 `SaveL10N`/`LoadL10N` 自动覆盖,无需新 API)

- [ ] **Step 1: 写失败测试**

`src/Luban.Tests/BaselineSidecarIOTests.cs`:

```csharp
using System.IO;
using Luban.Incremental;
using Xunit;

public class BaselineSidecarIOTests
{
    [Fact]
    public void KeyEntries_序列化往返_保序保墓碑()
    {
        string path = Path.Combine(Path.GetTempPath(), $"l10n_test_{System.Guid.NewGuid():N}.json");
        try
        {
            var s = new L10NSidecar { SignatureId = "sig1" };
            s.KeyEntries.Add(new KeyEntry { Key = "a" });
            s.KeyEntries.Add(new KeyEntry { Key = "b", Deleted = true });
            BaselineSidecarIO.SaveL10N(path, s);

            var loaded = BaselineSidecarIO.LoadL10N(path);
            Assert.Equal(2, loaded.KeyEntries.Count);
            Assert.Equal("a", loaded.KeyEntries[0].Key);
            Assert.False(loaded.KeyEntries[0].Deleted);
            Assert.Equal("b", loaded.KeyEntries[1].Key);
            Assert.True(loaded.KeyEntries[1].Deleted);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void 旧格式sidecar_无KeyEntries_读入为空且不报错()
    {
        string path = Path.Combine(Path.GetTempPath(), $"l10n_old_{System.Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, @"{""SignatureId"":""s"",""Keys"":[""a""],""Languages"":{}}");
            var loaded = BaselineSidecarIO.LoadL10N(path);
            Assert.NotNull(loaded);
            Assert.Empty(loaded.KeyEntries);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
```

- [ ] **Step 2: 跑测试确认失败/通过情况**

Run: `cd src && dotnet test Luban.Tests`
Expected: 若 `SaveL10N`/`LoadL10N` 是直接 JSON 序列化整个对象,测试可能直接 PASS;若 PASS 也继续 Step 3 检查(确认序列化选项不含忽略默认值的设置,墓碑 `Deleted=false` 不会丢)。若有 TypeNameHandling/构造函数问题则修复。

- [ ] **Step 3: 检查 BaselineSidecarIO 实现**

打开 `src/Luban.Core/Incremental/BaselineSidecarIO.cs`,确认 `SaveL10N`/`LoadL10N` 用 `System.Text.Json` 序列化 `L10NSidecar` 全对象。若序列化选项带 `DefaultIgnoreCondition`,确保 `KeyEntry.Deleted=false` 不被忽略(否则墓碑复活判定依赖默认值,可给 `Deleted` 加 `[JsonPropertyName]` 或改用 Always 序列化该属性)。按需修改后重跑测试。

- [ ] **Step 4: Commit**

```bash
git add src/Luban.Core/Incremental/BaselineSidecarIO.cs src/Luban.Tests/BaselineSidecarIOTests.cs
git commit -m "feat: L10N sidecar v2 KeyEntries 持久化往返"
```

---

### Task 3: L10NSpace 配置模型与解析

**Files:**
- Create: `src/Luban.Core/L10N/L10NSpace.cs`
- Create: `src/Luban.Core/L10N/L10NSpaceParser.cs`
- Modify: `src/Luban.Core/GenerationContext.cs`(成员与初始化)
- Test: `src/Luban.Tests/L10NSpaceParserTests.cs`

**Interfaces:**
- Produces: `L10NSpace`(字段见"关键跨任务接口")、`L10NSpaceParser.Parse() -> List<L10NSpace>`、`GenerationContext.L10NSpaces`、`GenerationContext.L10NTextIndexEnabled`

- [ ] **Step 1: 写失败测试**

`src/Luban.Tests/L10NSpaceParserTests.cs`(EnvManager 选项注入方式见 Step 3 说明;测试直接构造 parser 的入参字典):

```csharp
using System.Collections.Generic;
using Luban.L10N;
using Xunit;

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
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd src && dotnet test Luban.Tests` → 编译失败。

- [ ] **Step 3: 实现**

`src/Luban.Core/L10N/L10NSpace.cs`:

```csharp
using System.Collections.Generic;

namespace Luban.L10N;

/// <summary>
/// 一个语言 key space:一组语言表 + 独立的下标注册表(sidecar)+ 输出目录。
/// </summary>
public class L10NSpace
{
    public string Name { get; set; } = "";

    /// <summary>语言表名单(表全名或短名,逗号分隔配置)。</summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>true=数组格式 bin + text 字段转 int;false=保持现有 string 字典格式。</summary>
    public bool IndexMode { get; set; }

    public List<string> Languages { get; set; } = new();

    public string OutputFile { get; set; } = "languageconfig";

    /// <summary>输出子目录(相对 dataTarget 输出根目录)。</summary>
    public string OutputDir { get; set; } = "";

    public string KeyFieldName { get; set; } = "key";

    public string KeyFieldDesc { get; set; } = "zh_CN";

    /// <summary>本 space 的 sidecar 路径(注册表 + 基准快照)。</summary>
    public string SidecarPath { get; set; } = "";

    /// <summary>LoadDatas 末尾构建的下标注册表(分配后)。</summary>
    public L10NKeyIndex KeyIndex { get; set; }
}
```

`src/Luban.Core/L10N/L10NKeyIndex.cs`:

```csharp
using System.Collections.Generic;
using Luban.Incremental;

namespace Luban.L10N;

/// <summary>
/// 已分配的下标注册表视图:Entries 位置 == 下标。
/// </summary>
public class L10NKeyIndex
{
    public List<KeyEntry> Entries { get; }

    public int Count => Entries.Count;

    public L10NKeyIndex(List<KeyEntry> entries)
    {
        Entries = entries;
    }

    /// <summary>活 key 查下标;墓碑或不存在返回 false。</summary>
    public bool TryGetIndex(string key, out int index)
    {
        for (int i = 0; i < Entries.Count; i++)
        {
            var e = Entries[i];
            if (!e.Deleted && string.Equals(e.Key, key, System.StringComparison.Ordinal))
            {
                index = i;
                return true;
            }
        }
        index = -1;
        return false;
    }
}
```

`src/Luban.Core/L10N/L10NSpaceParser.cs`(测试直接传字典;GenerationContext 从 EnvManager 收集同前缀选项):

```csharp
using System;
using System.Collections.Generic;
using System.Linq;

namespace Luban.L10N;

/// <summary>
/// 解析 l10n.spaces 及 l10n.{space}.* 分组选项。未配置 l10n.spaces 时返回空表(走旧单值选项路径)。
/// </summary>
public static class L10NSpaceParser
{
    public static List<L10NSpace> Parse(IReadOnlyDictionary<string, string> options)
    {
        var result = new List<L10NSpace>();
        if (!options.TryGetValue("l10n.spaces", out var spacesStr) || string.IsNullOrWhiteSpace(spacesStr))
        {
            return result;
        }
        foreach (var name in spacesStr.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string P(string key, string def = "") =>
                options.TryGetValue($"l10n.{name}.{key}", out var v) && !string.IsNullOrWhiteSpace(v) ? v : def;

            var space = new L10NSpace
            {
                Name = name,
                Tables = P("tables").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                IndexMode = string.Equals(P("indexMode", "false"), "true", StringComparison.OrdinalIgnoreCase),
                Languages = P("languages").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
                OutputFile = P("outputFile", "languageconfig"),
                OutputDir = P("outputDir", name),
                KeyFieldName = P("keyFieldName", "key"),
                KeyFieldDesc = P("keyFieldDesc", "zh_CN"),
                SidecarPath = P("sidecar"),
            };
            result.Add(space);
        }
        return result;
    }
}
```

`GenerationContext.cs` 修改(在现有 `L10NLanguages` 赋值处附近,即约 469 行 `L10NLanguages = L10NOptionUtil.GetLanguages();` 之后):

```csharp
// 多 space 配置(l10n.spaces);未配置时空表,走旧单值选项路径
L10NSpaces = L10NSpaceParser.Parse(CollectL10NOptions());
L10NTextIndexEnabled = L10NSpaces.Any(s => s.IndexMode);
```

并新增私有方法(EnvManager 选项收集——查看 `EnvManager.Current` 的 API,用其遍历或 TryGetOption 收集 `l10n.` 前缀;若 EnvManager 无枚举接口,则改为在解析处逐 key TryGetOption,以 spaces 名单为驱动,只需查询已知 key 集合,不需要枚举):

```csharp
private Dictionary<string, string> CollectL10NOptions()
{
    var dict = new Dictionary<string, string>(StringComparer.Ordinal);
    var env = EnvManager.Current;
    string Probe(string key)
    {
        var v = env.GetOptionOrDefault("", key, true, "");
        return v ?? "";
    }
    dict["l10n.spaces"] = Probe("l10n.spaces");
    foreach (var name in Probe("l10n.spaces").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
    {
        foreach (var k in new[] { "tables", "indexMode", "languages", "outputFile", "outputDir", "keyFieldName", "keyFieldDesc", "sidecar" })
        {
            dict[$"l10n.{name}.{k}"] = Probe($"l10n.{name}.{k}");
        }
    }
    return dict;
}
```

> 注意:先读 `src/Luban.Core/Utils/EnvManager.cs`(或同目录)确认 `GetOptionOrDefault` 的 family/key 参数语义(本项目里选项常带 family 前缀,`BuiltinOptionNames.L10NFamily = "l10n"`)。若 `GetOptionOrDefault("", "l10n.main.tables", ...)` 不能命中(因为 family 拆分方式不同),按 EnvManager 实际规则调整 Probe 的参数——**以单元测试为准的 Parse 不受影响,只有 Collect 受影响**,Collect 写错会在 Task 13 集成验证时暴露,届时修正。

新增成员声明(放在 `L10NLanguages` 属性旁):

```csharp
public IReadOnlyList<L10N.L10NSpace> L10NSpaces { get; private set; } = Array.Empty<L10N.L10NSpace>();

public bool L10NTextIndexEnabled { get; private set; }
```

- [ ] **Step 4: 跑测试通过 + 全量构建**

Run: `cd src && dotnet test Luban.Tests && dotnet build Luban.sln`
Expected: 测试 PASS,解决方案编译通过。

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/L10N src/Luban.Core/GenerationContext.cs src/Luban.Tests/L10NSpaceParserTests.cs
git commit -m "feat: L10NSpace 多 key space 配置模型与解析"
```

---

### Task 4: 注册表构建 + text→int 转换层挂进 LoadDatas

**Files:**
- Create: `src/Luban.Core/L10N/TextKeyIndexTransformer.cs`
- Create: `src/Luban.Core/L10N/L10NKeyIndexBuilder.cs`
- Modify: `src/Luban.Core/GenerationContext.cs`(`LoadDatas()` 内插钩子)
- Test: `src/Luban.Tests/TextKeyIndexTransformerTests.cs`

**Interfaces:**
- Consumes: `L10NSpace`、`KeyIndexAllocator`(Task 1/3)
- Produces: `L10NKeyIndexBuilder.Build(ctx, space) -> L10NKeyIndex`(分配 + 挂到 `space.KeyIndex`);`TextKeyIndexTransformer`(IDataFuncVisitor2<DType>);LoadDatas 时序 `LoadDatas → BuildKeyIndexes+Transform → CalculateTableChecksums`

- [ ] **Step 1: 写失败测试(transformer 纯逻辑)**

`src/Luban.Tests/TextKeyIndexTransformerTests.cs`:

```csharp
using Luban.Datas;
using Luban.L10N;
using Luban.Types;
using Xunit;

public class TextKeyIndexTransformerTests
{
    private static readonly Dictionary<string, int> Map = new() { ["k1"] = 3, ["k2"] = 7 };

    [Fact]
    public void text标签的string_转DInt下标()
    {
        var type = TString.Create(false, new Dictionary<string, string> { { "text", "1" } });
        var result = new TextKeyIndexTransformer(Map, "Tb", "f1", "row1").Apply(
            DString.ValueOf(type, "k1"), type);
        Assert.IsType<DInt>(result);
        Assert.Equal(3, ((DInt)result).Value);
    }

    [Fact]
    public void 无text标签_原样返回()
    {
        var type = TString.Create(false, null);
        var data = DString.ValueOf(type, "k1");
        Assert.Same(data, new TextKeyIndexTransformer(Map, "Tb", "f1", "row1").Apply(data, type));
    }

    [Fact]
    public void 未知key_抛异常带上下文()
    {
        var type = TString.Create(false, new Dictionary<string, string> { { "text", "1" } });
        var ex = Assert.Throws<Luban.L10N.TextKeyIndexException>(() =>
            new TextKeyIndexTransformer(Map, "TbItem", "nameId", "id=1001").Apply(
                DString.ValueOf(type, "nope"), type));
        Assert.Contains("TbItem", ex.Message);
        Assert.Contains("nameId", ex.Message);
        Assert.Contains("nope", ex.Message);
    }

    [Fact]
    public void 空值_转负一哨兵()
    {
        var type = TString.Create(false, new Dictionary<string, string> { { "text", "1" } });
        var result = new TextKeyIndexTransformer(Map, "Tb", "f", "r").Apply(DString.ValueOf(type, ""), type);
        Assert.Equal(-1, ((DInt)result).Value);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `cd src && dotnet test Luban.Tests` → 编译失败。

- [ ] **Step 3: 实现**

`src/Luban.Core/L10N/TextKeyIndexException.cs`:

```csharp
using System;

namespace Luban.L10N;

/// <summary>text 字段语言 key 无法解析为下标时抛出(含表/字段/行上下文)。</summary>
public class TextKeyIndexException : Exception
{
    public TextKeyIndexException(string message) : base(message) { }
}
```

`src/Luban.Core/L10N/TextKeyIndexTransformer.cs`(模式照抄 `TextKeyToValueTransformer`,仅 DString 分支生效):

```csharp
using System.Collections.Generic;
using Luban.Datas;
using Luban.DataTransformer;
using Luban.DataVisitors;
using Luban.Types;

namespace Luban.L10N;

/// <summary>
/// 把 text 标签的 DString(语言 key)改写为 DInt(注册表下标)。
/// Apply 入口的 (table, field, row) 仅用于错误信息;容器/bean 内嵌 text 字段经由整体重建路径处理。
/// </summary>
public class TextKeyIndexTransformer : DataTransfomerBase, IDataFuncVisitor2<DType>
{
    private readonly Dictionary<string, int> _keyToIndex;
    private readonly string _table;
    private readonly string _field;
    private readonly string _row;

    public TextKeyIndexTransformer(Dictionary<string, int> keyToIndex, string table, string field, string row)
    {
        _keyToIndex = keyToIndex;
        _table = table;
        _field = field;
        _row = row;
    }

    DType IDataFuncVisitor2<DType>.Accept(DString data, TType type)
    {
        if (!type.HasTag("text"))
        {
            return data;
        }
        var key = data.Value;
        if (string.IsNullOrEmpty(key))
        {
            return DInt.ValueOf(type, -1); // 空 text 字段哨兵;调用方记 WARN
        }
        if (_keyToIndex.TryGetValue(key, out var index))
        {
            return DInt.ValueOf(type, index);
        }
        throw new TextKeyIndexException(
            $"[lan-index] 表 {_table} 字段 {_field} 行 {_row} 的语言 key '{key}' 不在下标注册表中,请检查语言表是否包含该 key");
    }
}
```

> 实现注意:打开 `src/Luban.Core/DataTransformer/DataTransfomerBase.cs` 确认基类默认行为(其余 Accept 是否原样返回)。若 `DInt.ValueOf(type, ...)` 不接受 TString 作为 type 参数(签名可能要求 TInt),改用 `DInt.ValueOf(TInt.Create(type.IsNullable, type.Tags), index)`——以实际签名为准,测试不变。

`src/Luban.Core/L10N/L10NKeyIndexBuilder.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Luban.Datas;
using Luban.Defs;
using Luban.Incremental;
using Luban.Types;

namespace Luban.L10N;

/// <summary>
/// LoadDatas 末尾:为每个 indexMode space 分配注册表,并把业务表 text 字段改写为 DInt。
/// 必须在 CalculateTableChecksums 之前执行(checksum 直接基于 int 数据,注册表变化必然反映进行哈希)。
/// </summary>
public static class L10NKeyIndexBuilder
{
    public static void Build(GenerationContext ctx)
    {
        if (ctx.L10NSpaces.Count == 0)
        {
            return;
        }
        foreach (var space in ctx.L10NSpaces.Where(s => s.IndexMode))
        {
            space.KeyIndex = new L10NKeyIndex(
                KeyIndexAllocator.Allocate(KeyIndexAllocator.LoadPrev(space.SidecarPath), CollectCurrentKeys(ctx, space)));
        }
        if (!ctx.L10NTextIndexEnabled)
        {
            return;
        }
        TransformTextFields(ctx);
    }

    /// <summary>当前语言表全部 key(space.Tables 名单内的表,按 key 字段取,去重)。</summary>
    private static IEnumerable<string> CollectCurrentKeys(GenerationContext ctx, L10NSpace space)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var table in MatchTables(ctx, space))
        {
            foreach (var rec in ctx.GetTableExportDataList(table))
            {
                if (rec.Data is DBean bean)
                {
                    var k = bean.GetField(space.KeyFieldName) as DString;
                    if (k != null && !string.IsNullOrEmpty(k.Value))
                    {
                        keys.Add(k.Value);
                    }
                }
            }
        }
        return keys;
    }

    /// <summary>业务表 text 字段 → DInt。引用 space 由 l10n.textRefSpace 指定(默认 main)。</summary>
    private static void TransformTextFields(GenerationContext ctx)
    {
        string refSpaceName = EnvManager.Current.GetOptionOrDefault("l10n", "textRefSpace", true, "main");
        var refSpace = ctx.L10NSpaces.FirstOrDefault(s =>
            string.Equals(s.Name, refSpaceName, StringComparison.Ordinal) && s.IndexMode);
        if (refSpace?.KeyIndex == null)
        {
            throw new TextKeyIndexException(
                $"[lan-index] l10n.textRefSpace='{refSpaceName}' 未命中任何 indexMode=true 的 space,无法解析 text 字段");
        }
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < refSpace.KeyIndex.Count; i++)
        {
            var e = refSpace.KeyIndex.Entries[i];
            if (!e.Deleted)
            {
                map[e.Key] = i;
            }
        }

        var spaceTableNames = new HashSet<string>(ctx.L10NSpaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
        foreach (var table in ctx.Tables)
        {
            if (spaceTableNames.Contains(table.Name) || spaceTableNames.Contains(table.FullName))
            {
                continue; // 语言表自身的 key 字段不转
            }
            foreach (var record in ctx.GetTableExportDataList(table))
            {
                if (record.Data is not DBean bean)
                {
                    continue;
                }
                var trans = new TextKeyIndexTransformer(map, table.FullName, "", $"{table.Name}[{record.Index}]");
                record.Data = (DBean)record.Data.Apply(trans, table.ValueTType);
            }
        }
    }

    public static List<DefTable> MatchTables(GenerationContext ctx, L10NSpace space)
    {
        return ctx.Tables.Where(t => space.Tables.Contains(t.Name) || space.Tables.Contains(t.FullName)).ToList();
    }
}
```

> 实现注意三点,动手前先核对:
> 1. `record.Index` 字段名以 `src/Luban.Core/Defs/Record.cs` 实际成员为准(可能是 `Index` 或主键属性,选能标识行的那个,用于错误信息)。
> 2. `DataTransfomerBase.Apply` 对 bean 的重建路径:确认 `record.Data.Apply(trans, table.ValueTType)` 能递归进 bean 字段并替换(照抄 `DefaultTextProvider.ProcessDatas` 的用法,它就是这么调的)。
> 3. 空 text 字段记 WARN:在 transformer 的空值分支加 `NLog.LogManager.GetCurrentClassLogger().Warn(...)` 带上下文,或返回哨兵后由调用方统计——取简单前者。

`GenerationContext.LoadDatas()` 修改(在 `DataLoaderManager.Ins.LoadDatas(this);` 与 `CalculateTableChecksums();` 之间插入):

```csharp
// L10N 下标注册表构建 + text 字段 → DInt(必须在 checksum 之前,保证行哈希基于 int)
L10N.L10NKeyIndexBuilder.Build(this);
```

- [ ] **Step 4: 跑测试 + 全量构建**

Run: `cd src && dotnet test Luban.Tests && dotnet build Luban.sln`
Expected: PASS + 编译通过。

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/L10N src/Luban.Core/GenerationContext.cs src/Luban.Tests/TextKeyIndexTransformerTests.cs
git commit -m "feat: text 字段语言 key→int 下标转换层 + LoadDatas 注册表构建钩子"
```

---

### Task 5: StructureSignature 字段行追加 tags

**Files:**
- Modify: `src/Luban.Core/TypeVisitors/StructureSignature.cs:57-60`
- Test: `src/Luban.Tests/StructureSignatureTests.cs`

**Interfaces:** Produces: string→text(或任意加 tag)导致 SignatureId 变化的保证(spec §4.7/风险3)。

- [ ] **Step 1: 写失败测试**

```csharp
using Luban.RawDefs;
using Luban.Types;
using Xunit;

public class StructureSignatureTests
{
    [Fact]
    public void 同字段类型_有无text标签_签名不同()
    {
        string WithTags(Dictionary<string, string> tags) =>
            TString.Create(false, tags).TypeName; // 占位:直接构造 TString 比较 TypeName 无意义,改为验证 AppendType 输出

        // 结构签名对 tags 的敏感性通过行为断言:同名字段,一个裸 string 一个 text 标签,
        // 两者参与签名计算的描述串必须不同。AppendType 是 private,通过公开入口验证:
        // 构造两个最小 DefTable 不可行(依赖 DefAssembly),故以反射调用 AppendType 验证。
        var method = typeof(Luban.TypeVisitors.StructureSignature).GetMethod("AppendType",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(method);
        var plain = (System.Text.StringBuilder)method.Invoke(null, new object[]
            { new System.Text.StringBuilder(), TString.Create(false, null), null });
        var tagged = (System.Text.StringBuilder)method.Invoke(null, new object[]
            { new System.Text.StringBuilder(), TString.Create(false, new() { { "text", "1" } }), null });
        Assert.NotEqual(plain.ToString(), tagged.ToString());
    }
}
```

> 若反射路径过于脆弱,可把 `AppendType` 改为 `internal` 并给测试工程加 `InternalsVisibleTo`(在 Luban.Core.csproj `<ItemGroup><InternalsVisibleTo Include="Luban.Tests" /></ItemGroup>`),测试直接调用。**推荐后者**,实现时统一采用。

- [ ] **Step 2: 跑测试确认失败**

Run: `cd src && dotnet test Luban.Tests` → FAIL(当前 AppendType 对 TString 都输出 `P:TString`,两者相同)。

- [ ] **Step 3: 实现**

`StructureSignature.cs` 的 `AppendType` default 分支前加 TString(以及带标签的任意原语类型)显式分支——最小改法:default 分支追加 tags:

```csharp
default:
    sb.Append("P:").Append(t.GetType().Name);
    // tags 参与签名:string→text 等仅加标签的类型迁移必须触发 SignatureId 变化(重新基准)
    if (t.Tags.Count > 0)
    {
        sb.Append("|tags=").Append(string.Join(",", t.Tags.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => kv.Key + "=" + kv.Value)));
    }
    break;
```

文件头 `using` 补 `System.Linq`(如缺)。

- [ ] **Step 4: 跑测试通过 + 构建**

Run: `cd src && dotnet test Luban.Tests && dotnet build Luban.sln`

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/TypeVisitors/StructureSignature.cs src/Luban.Tests/StructureSignatureTests.cs src/Luban.Core/Luban.Core.csproj
git commit -m "feat: StructureSignature 纳入字段 tags,保证 string→text 迁移触发重新基准"
```

---

### Task 6: cs-bin text→int 代码生成特判

**Files:**
- Modify: `src/Luban.CSharp/TypeVisitors/DeclaringTypeNameVisitor.cs`(TString 分支)
- Modify: `src/Luban.CSharp/TypeVisitors/UnderlyingDeclaringTypeNameVisitor.cs:71-75`
- Modify: `src/Luban.CSharp/TypeVisitors/BinaryUnderlyingDeserializeVisitor.cs:70-74`
- Create: `src/Luban.CSharp/TypeVisitors/L10NTextIndexTypeUtil.cs`

**Interfaces:**
- Produces: indexMode 下 cs 代码中 text 字段声明为 `int`、反序列化 `ReadInt()`(varint,与 ByteBuf.WriteInt 对齐)

- [ ] **Step 1: 工具类**

`src/Luban.CSharp/TypeVisitors/L10NTextIndexTypeUtil.cs`:

```csharp
using Luban.Types;

namespace Luban.CSharp.TypeVisitors;

/// <summary>L10N index 模式下,text 标签的 string 字段在 C# 中按 int 处理。</summary>
public static class L10NTextIndexTypeUtil
{
    public static bool IsTextIndex(TType type)
    {
        return GenerationContext.Current.L10NTextIndexEnabled && type is TString && type.HasTag("text");
    }
}
```

- [ ] **Step 2: 三个访问器特判**

`UnderlyingDeclaringTypeNameVisitor.Accept(TString)`:

```csharp
public string Accept(TString type)
{
    if (L10NTextIndexTypeUtil.IsTextIndex(type))
    {
        return "int";
    }
    return "string";
}
```

`DeclaringTypeNameVisitor.Accept(TString)`:同样在开头加 `if (L10NTextIndexTypeUtil.IsTextIndex(type)) { return "int"; }`(可空包装 `int?` 由该访问器外层 nullable 逻辑统一处理——打开文件确认其可空处理方式,若它对 string 特判了 `string?` 则 int 对应 `int?`,逻辑照搬)。

`BinaryUnderlyingDeserializeVisitor.Accept(TString, bufName, fieldName, depth)`:

```csharp
public string Accept(TString type, string bufName, string fieldName, int depth)
{
    if (L10NTextIndexTypeUtil.IsTextIndex(type))
    {
        return $"{fieldName} = {bufName}.ReadInt();";
    }
    return $"{fieldName} = {bufName}.ReadString();";
}
```

> 检查项:打开 `src/Luban.CSharp/Templates/cs-bin/bean.sbn`(或 stub/加载模板),确认没有对 string 字段的硬编码模板分支绕过访问器;若有(如默认值/构造器初始化),同样以 `L10NTextIndexTypeUtil` 特判。`NeedInitFieldVisitor`/`CtorDefaultValueVisitor` 若对 string 有 init 语义,对 text-int 返回与 int 相同的结果——打开确认,`string` 分支加 `if (IsTextIndex) 走 TInt 分支逻辑`。改动控制在"每个访问器开头一个 if"。

- [ ] **Step 3: 构建 + 手工冒烟(mini conf 前置)**

Run: `cd src && dotnet build Luban.sln`

mini conf(Task 13 前各任务都要用它冒烟,现在先建):

- `tests/l10n-miniconf/conf.json`:

```json
{
  "groups": [ { "names": ["c"], "default": true } ],
  "schemaFiles": [ { "fileName": "schema.xml", "type": "" } ],
  "dataDir": "data",
  "targets": [ { "name": "client", "manager": "Tables", "groups": ["c"], "topModule": "Test" } ]
}
```

- `tests/l10n-miniconf/schema.xml`:

```xml
<root>
  <bean name="Language">
    <var name="key" type="string"/>
    <var name="zh_CN" type="string" group="zh_CN"/>
    <var name="en_US" type="string" group="en_US"/>
  </bean>
  <bean name="Item">
    <var name="id" type="int"/>
    <var name="nameId" type="text"/>
  </bean>
  <table name="LanguageText" mode="map" value="Language" input="lang.csv">
    <index name="Id" field="key"/>
  </table>
  <table name="TbItem" mode="map" value="Item" input="item.csv">
    <index name="id" field="id"/>
  </table>
</root>
```

- `tests/l10n-miniconf/data/lang.csv`:

```csv
##var,key,zh_CN,en_US
,key1,你好,Hello
,key2,再见,Bye
```

- `tests/l10n-miniconf/data/item.csv`:

```csv
##var,id,nameId
,1,key1
,2,key2
```

冒烟命令(生成代码,检查 text 字段为 int):

```bash
cd tests/l10n-miniconf && dotnet ../../src/Luban/bin/Debug/net8.0/Luban.dll --conf conf.json -t client -c cs-bin -f -x outputCodeDir=out_code -x l10n.spaces=main -x l10n.main.tables=LanguageText -x l10n.main.indexMode=true -x l10n.main.languages=zh_CN,en_US -x l10n.main.sidecar=l10n.json
```

Expected: `out_code/Item.cs`(或 stub 目录)中 `nameId` 声明为 `public int nameId;`,加载代码含 `nameId = buf.ReadInt();`;**且此命令会先跑数据加载,若 Task 4 的转换层有未知 key 会在此暴露**(key1/key2 都在 lang.csv,应通过)。

- [ ] **Step 4: Commit**

```bash
git add src/Luban.CSharp/TypeVisitors tests/l10n-miniconf
git commit -m "feat: cs-bin 在 L10N index 模式下将 text 字段生成为 int + mini conf 冒烟环境"
```

---

### Task 7: java-json text→int 特判

**Files:**
- Modify: `src/Luban.Java/TypeVisitors/JavaDeclaringTypeNameVisitor.cs:72-75`
- Modify: `src/Luban.Java/TypeVisitors/JavaJsonUnderlyingDeserializeVisitor.cs:71-74`

**Interfaces:** Produces: indexMode 下 Java 字段 `int` + `getAsInt()`。

- [ ] **Step 1: 特判两个访问器**

`JavaDeclaringTypeNameVisitor.Accept(TString)` 开头:

```csharp
if (GenerationContext.Current.L10NTextIndexEnabled && type.HasTag("text"))
{
    return "int";
}
return "String";
```

`JavaJsonUnderlyingDeserializeVisitor.Accept(TString, json, x, depth)` 开头:

```csharp
if (GenerationContext.Current.L10NTextIndexEnabled && type.HasTag("text"))
{
    return $"{x} = {json}.getAsInt();";
}
return $"{x} = {json}.getAsString();";
```

(JLuban 项目无独立 util,直接内联条件,与该工程现有风格一致。)

- [ ] **Step 2: 构建 + json 冒烟**

```bash
cd src && dotnet build Luban.sln
cd ../tests/l10n-miniconf && dotnet ../../src/Luban/bin/Debug/net8.0/Luban.dll --conf conf.json -t client -c java-json -d json -f -x outputDataDir=out_json -x outputCodeDir=out_jcode -x java-json.codePackage=test -x l10n.spaces=main -x l10n.main.tables=LanguageText -x l10n.main.indexMode=true -x l10n.main.languages=zh_CN,en_US -x l10n.main.sidecar=l10n.json
```

Expected: `out_json/tbitem.json` 中 `nameId` 为数字;`out_jcode` 的 Item.java 中 `public int nameId;`。

- [ ] **Step 3: Commit**

```bash
git add src/Luban.Java/TypeVisitors
git commit -m "feat: java-json 在 L10N index 模式下将 text 字段生成为 int"
```

---

### Task 8: 数组格式语言 bin(基准路径)

**Files:**
- Modify: `src/Luban.Core/Incremental/L10NChecksumUtil.cs`(数组序列化 + per-space 入口)
- Modify: `src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs`(indexMode 分支)
- Test: `src/Luban.Tests/LanguageArraySerializationTests.cs`

**Interfaces:**
- Produces: `L10NChecksumUtil.SerializeLanguageArray(IReadOnlyList<KeyEntry> entries, Dictionary<object,string> values) -> byte[]`(布局 `{varint count}{value*}`,墓碑/缺失=空串);`L10NBinarySplitDataExporter.ExportL10NArrayPerLanguage(ctx, space, tables, manifest)`(写 `{outputDir}/{lang}/{outputFile}.bytes`)

- [ ] **Step 1: 写失败测试**

```csharp
using System.Collections.Generic;
using Luban.Incremental;
using Luban.Serialization;
using Xunit;

public class LanguageArraySerializationTests
{
    [Fact]
    public void 数组布局_按下标写值_墓碑为空串()
    {
        var entries = new List<KeyEntry>
        {
            new() { Key = "a" }, new() { Key = "dead", Deleted = true }, new() { Key = "c" },
        };
        var values = new Dictionary<object, string> { ["a"] = "VA", ["c"] = "VC" };
        byte[] bytes = L10NChecksumUtil.SerializeLanguageArray(entries, values);

        var buf = new ByteBuf(bytes);
        Assert.Equal(3, buf.ReadSize());
        Assert.Equal("VA", buf.ReadString());
        Assert.Equal("", buf.ReadString());
        Assert.Equal("VC", buf.ReadString());
        Assert.Equal(0, buf.BytesAvailable);
    }
}
```

- [ ] **Step 2: 跑测试确认失败** → 编译失败。

- [ ] **Step 3: 实现**

`L10NChecksumUtil.cs` 加方法(放 `SerializeLanguageBytes` 旁):

```csharp
/// <summary>
/// index 模式语言文件序列化:[WriteSize: count(注册表长度含墓碑)] [WriteString(value)]*。
/// 值按注册表位置对齐;墓碑/缺失语言 = 空串。与导出的 {lang}/{outputFile}.bytes 逐字节一致。
/// </summary>
public static byte[] SerializeLanguageArray(IReadOnlyList<KeyEntry> entries, Dictionary<object, string> values)
{
    var buf = new ByteBuf();
    buf.WriteSize(entries.Count);
    foreach (var e in entries)
    {
        string v = e.Deleted || values == null || !values.TryGetValue(e.Key, out var val) ? "" : val ?? "";
        buf.WriteString(v);
    }
    return buf.CopyData();
}
```

`L10NBinarySplitDataExporter.cs` 加 indexMode 导出(新增内部静态方法,现有方法不动):

```csharp
/// <summary>
/// index 模式:按 space 注册表顺序导出每语言数组 bin:{outputDir}/{lang}/{outputFile}.bytes。
/// 值来自 space.Tables 名单表的合并(沿用 mergeOutput 语义:跨表 key 互斥校验)。
/// </summary>
internal static void ExportL10NArrayPerLanguage(GenerationContext ctx, Luban.L10N.L10NSpace space, OutputFileManifest manifest)
{
    if (space.KeyIndex == null || space.Languages.Count == 0)
    {
        return;
    }
    var mergedTables = L10N.L10NKeyIndexBuilder.MatchTables(ctx, space)
        .Where(t => t.ValueTType is TBean).ToList();

    // 跨表重复 key 校验(照搬 ExportL10NMergedPerLanguage 的 seenKeys 逻辑,重复抛异常)
    var seen = new HashSet<string>(StringComparer.Ordinal);
    foreach (var t in mergedTables)
    {
        foreach (var r in ctx.GetTableExportDataList(t))
        {
            if (r.Data is DBean b && b.GetField(space.KeyFieldName) is DString k &&
                !string.IsNullOrEmpty(k.Value) && !seen.Add(k.Value))
            {
                throw new Exception($"[lan-index] space '{space.Name}' 表 {t.FullName} 存在重复 key '{k.Value}',合并导出要求各语言表 key 互斥");
            }
        }
    }

    foreach (var lang in space.Languages)
    {
        var values = new Dictionary<object, string>();
        foreach (var t in mergedTables)
        {
            foreach (var r in ctx.GetTableExportDataList(t))
            {
                if (r.Data is not DBean b)
                {
                    continue;
                }
                var k = b.GetField(space.KeyFieldName) as DString;
                if (k == null || string.IsNullOrEmpty(k.Value))
                {
                    continue;
                }
                var v = b.GetField(lang) as DString;
                values[k.Value] = v?.Value ?? "";
            }
        }
        byte[] bytes = Incremental.L10NChecksumUtil.SerializeLanguageArray(space.KeyIndex.Entries, values);
        manifest.AddFile(new OutputFile
        {
            File = Path.Combine(space.OutputDir, lang, space.OutputFile + ".bytes"),
            Content = bytes,
        });
    }
}
```

> `Luban.DataTarget.Builtin` 引用 `Luban.Incremental`(L10NChecksumUtil 所在命名空间)——项目已有引用(IncrementalL10NDataExporter 在用),确认无需改 csproj。

- [ ] **Step 4: 跑测试 + 构建 + mini conf 冒烟**

```bash
cd src && dotnet test Luban.Tests && dotnet build Luban.sln
cd ../tests/l10n-miniconf && dotnet ../../src/Luban/bin/Debug/net8.0/Luban.dll --conf conf.json -t client -d bin -f -x bin.outputDataDir=out_bin -x dataExporter=l10n-baseline-with-sidecar -x l10n.spaces=main -x l10n.main.tables=LanguageText -x l10n.main.indexMode=true -x l10n.main.languages=zh_CN,en_US -x l10n.main.sidecar=l10n.json -x l10n.main.outputDir=language -x incremental.sidecarPath=l10n.json
```

Expected: 此命令仍走旧路径(Task 9 才接 omnibus),此处仅确认编译与测试。数组导出的接通验证放 Task 9。

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/Incremental/L10NChecksumUtil.cs src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs src/Luban.Tests/LanguageArraySerializationTests.cs
git commit -m "feat: index 模式语言数组 bin 序列化(注册表对齐+墓碑空串)"
```

---

### Task 9: LLP2 patch + 增量导出器 indexMode 改造(注册表回写)

**Files:**
- Modify: `src/Luban.DataTarget.Builtin/Incremental/PatchFormat.cs`(加 MagicL10N2)
- Modify: `src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs`(indexMode 分支:LLP2 + 回写)
- Modify: `src/Luban.DataTarget.Builtin/Incremental/L10NBaselineWithSidecarExporter.cs`(sidecar v2 写 KeyEntries)
- Test: `src/Luban.Tests/LLP2FormatTests.cs`

**Interfaces:**
- Produces: `PatchFormat.MagicL10N2 = "LLP2"`;LLP2 布局 `magic + string sigId + varint upsertCount + (varint idx, string val)* + varint delCount + varint idx*`;sidecar KeyEntries 在基准与增量 run 均回写

- [ ] **Step 1: 写失败测试(布局字节)**

```csharp
using Luban.DataExporter.Builtin.Incremental;
using Luban.Serialization;
using Xunit;

public class LLP2FormatTests
{
    [Fact]
    public void 布局_下标与值()
    {
        var buf = new ByteBuf();
        PatchFormat.WriteMagic(buf, PatchFormat.MagicL10N2);
        buf.WriteString("SIG");
        buf.WriteSize(1);
        buf.WriteInt(7);
        buf.WriteString("V7");
        buf.WriteSize(1);
        buf.WriteInt(3);

        var r = new ByteBuf(buf.CopyData());
        Assert.Equal("LLP2", System.Text.Encoding.UTF8.GetString(r.ReadBytes(4)));
        Assert.Equal("SIG", r.ReadString());
        Assert.Equal(1, r.ReadSize());
        Assert.Equal(7, r.ReadInt());
        Assert.Equal("V7", r.ReadString());
        Assert.Equal(1, r.ReadSize());
        Assert.Equal(3, r.ReadInt());
        Assert.Equal(0, r.BytesAvailable);
    }
}
```

- [ ] **Step 2: 跑测试确认失败**(MagicL10N2 不存在)。

- [ ] **Step 3: 实现**

`PatchFormat.cs` 加:

```csharp
/// <summary>
/// L10N index 模式 delta patch magic(4 字节 ASCII "LLP2")。全下标,无字符串 key。
/// </summary>
public const string MagicL10N2 = "LLP2";
```

`IncrementalL10NDataExporter.Handle` 改造:开头按 ctx.L10NSpaces 分流——存在 indexMode space 时走新私有方法 `HandleIndexMode(ctx, dataTarget, manifest)`,否则原逻辑不动。`HandleIndexMode` 对每个 indexMode space:

```csharp
private static void HandleIndexMode(GenerationContext ctx, OutputFileManifest manifest, string sidecarPath)
{
    foreach (var space in ctx.L10NSpaces.Where(s => s.IndexMode))
    {
        var baseline = BaselineSidecarIO.LoadL10N(space.SidecarPath);
        string curSig = StructureSignature.ComputeForTable(L10N.L10NKeyIndexBuilder.MatchTables(ctx, space).First(t => t.ValueTType is TBean));
        if (string.IsNullOrEmpty(baseline.SignatureId) || curSig != baseline.SignatureId)
        {
            throw new InvalidOperationException(
                $"[增量导出已终止] L10N space '{space.Name}' 结构变化:SignatureId 期望 {baseline.SignatureId} 实际 {curSig}。请重新执行基准导出。");
        }

        // 当前值: space 名单表合并 -> lang -> (key, value)
        var perLang = BuildCurrentPerLang(ctx, space); // Dictionary<string, Dictionary<object,string>>,镜像 BuildPerLanguageMap 但只取 space 表

        // 基准快照对齐: baseline.Keys[i] 的下标 = KeyEntries 中该 key 的位置(基准时刻)
        var entries = space.KeyIndex.Entries;
        int baseCount = baseline.Keys.Count; // 基准时刻注册表长度 = 基准快照 Keys 数
        var keyToPos = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Count; i++) keyToPos[entries[i].Key] = i;

        var langStamps = ctx.GetL10NLangStamps();
        foreach (var lang in space.Languages)
        {
            var cur = perLang.GetValueOrDefault(lang) ?? new Dictionary<object, string>();
            var baseHashes = baseline.Languages.GetValueOrDefault(lang)?.Hashes;
            var upserts = new List<(int, string)>();
            var deletes = new List<int>();

            // 1) 基准快照内的 key:比对 hash
            for (int i = 0; i < baseCount && baseHashes != null; i++)
            {
                var k = baseline.Keys[i];
                var idx = keyToPos.GetValueOrDefault(k, -1);
                var curVal = cur.TryGetValue(k, out var v) && idx >= 0 && !entries[idx].Deleted ? v : null;
                if (curVal == null)
                {
                    deletes.Add(idx >= 0 ? idx : i); // 基准有、当前无(或已墓碑)
                }
                else
                {
                    var md5 = FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(curVal ?? ""));
                    if (baseHashes[i] != md5)
                    {
                        upserts.Add((idx, curVal));
                    }
                }
            }
            // 2) 基准之后追加的 key(下标 >= baseCount 或不在基准快照):全部 upsert
            for (int i = baseCount; i < entries.Count; i++)
            {
                var e = entries[i];
                if (e.Deleted) continue;
                if (cur.TryGetValue(e.Key, out var v))
                {
                    upserts.Add((i, v ?? ""));
                }
            }

            if (upserts.Count == 0 && deletes.Count == 0) continue;

            var buf = new ByteBuf();
            PatchFormat.WriteMagic(buf, PatchFormat.MagicL10N2);
            buf.WriteString(baseline.SignatureId);
            buf.WriteSize(upserts.Count);
            foreach (var (idx, val) in upserts) { buf.WriteInt(idx); buf.WriteString(val ?? ""); }
            buf.WriteSize(deletes.Count);
            foreach (var idx in deletes) buf.WriteInt(idx);

            string patchFile = $"{space.OutputDir}/{lang}/{space.OutputFile}.patch.bytes";
            manifest.AddFile(new OutputFile { File = patchFile, Content = buf.CopyData() });
            // ChangedTables 条目 Table = lang,Stamp 取 langStamps(保留现有语义)
        }
    }
    // _delta.manifest 输出与普通表合并(omnibus-incremental 统一处理,本导出器单独使用时仍写 _l10n.delta.manifest)
}
```

> 实现注意:
> 1. 上面的骨架里 `deletes.Add(idx >= 0 ? idx : i)` 有歧义陷阱——基准 key 在当前注册表必然存在(注册表只追加,LoadPrev 来自同一 sidecar),`idx < 0` 只会出现在"基准快照有、注册表却没有"的异常态(手工编辑 sidecar),此时**抛异常**而不是猜测下标。实现时写成显式检查。
> 2. `BuildCurrentPerLang` 是 `L10NChecksumUtil.BuildPerLanguageMap` 的 space 限定版:遍历 `MatchTables(ctx, space)` 而非 `ctx.Tables`。直接给 BuildPerLanguageMap 加个 `tables` 可选参数(默认 null=全部,保持旧行为),增量导出器传 space 表——比重写一份更 DRY。
> 3. **注册表回写**(幂等关键):HandleIndexMode 末尾,对每个 space `baseline.KeyEntries = space.KeyIndex.Entries; baselineSidecarIO.SaveL10N(space.SidecarPath, baseline)`(基准快照 Languages/Keys 不动,只更新 KeyEntries)。注意 LoadL10N 之后改对象再 Save 即可。
> 4. per-language stamp:沿用 `ctx.GetL10NLangStamps()`(多 space 后它算的是全部表——本任务先保持单 space 语义:mini conf 只有一个 main;slg 三 space 的 per-space stamp 在 Task 11 omnibus 里改为按 space 计算,`ComputePerLanguageFileMd5` 加 space 限定参数)。

`L10NBaselineWithSidecarExporter.WriteL10NSidecar` 改造:签名加 space 参数或循环 ctx.L10NSpaces;indexMode space:
- `Keys` = 注册表活 key 按位置(**基准快照 = 本次基准时刻注册表全量**,含墓碑的写法:Keys 只放活 key 但 Hashes 与 Keys 对齐——注意墓碑在基准快照里的表示:**Keys/Hashes 只含活 key、紧凑排列**;增量侧通过 key→注册表位置换算下标。这保持现有 sidecar 基准快照结构不变,增量 diff 逻辑如上)。
- `KeyEntries` = space.KeyIndex.Entries(回写注册表)。
- `Languages[lang].Hashes` = 与 Keys 对齐的 MD5(值取数组序列化对应槽)。
- 未配 spaces 时(旧路径)行为完全不变。

- [ ] **Step 4: 跑测试 + 构建**

```bash
cd src && dotnet test Luban.Tests && dotnet build Luban.sln
```

- [ ] **Step 5: Commit**

```bash
git add src/Luban.DataTarget.Builtin/Incremental src/Luban.Tests/LLP2FormatTests.cs
git commit -m "feat: LLP2 全下标语言增量 patch + sidecar KeyEntries 注册表回写"
```

---

### Task 10: omnibus-baseline 组合导出器

**Files:**
- Modify: `src/Luban.DataTarget.Builtin/Incremental/BaselineWithSidecarExporter.cs`(加 TableFilter 钩子)
- Create: `src/Luban.DataTarget.Builtin/Incremental/OmnibusBaselineExporter.cs`

**Interfaces:**
- Consumes: Task 8 数组导出、Task 9 sidecar v2、现有 `BaselineWithSidecarExporter`/`L10NBinarySplitDataExporter`
- Produces: `[DataExporter("omnibus-baseline")]` —— 普通表(排除 space 表)走 baseline-with-sidecar;indexMode space 走数组+sidecar v2;非 indexMode space 走旧 string 字典;每 space 在自己 outputDir 下输出 checksumconfig.bytes

- [ ] **Step 1: 现有导出器加表过滤钩子**

`BaselineWithSidecarExporter.cs` 加属性(无参构造路径不受影响):

```csharp
/// <summary>omnibus 组合时排除语言 space 表(由组合导出器接管);null=不过滤。</summary>
public Func<DefTable, bool> TableFilter { get; set; }
```

在其 `Handle`(以及它包装的默认导出路径)遍历表处应用过滤——打开该文件,找到遍历 `ctx.Tables`/`ctx.ExportTables` 的位置,包一层 `Where(t => TableFilter == null || !TableFilter(t))`。同样给 `L10NBinarySplitDataExporter` 的表遍历处加同名钩子(它被非 indexMode space 复用时反向过滤:只留该 space 的表)。

- [ ] **Step 2: OmnibusBaselineExporter**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Luban.DataTarget;
using Luban.Defs;
using Luban.L10N;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 组合基准导出器:一次 run 同时产出普通表 + 多语言 space。
/// 路由:不在任何 space 名单的表 -> baseline-with-sidecar(现有逻辑,排除 space 表);
/// indexMode space -> 数组格式 bin + sidecar v2(含注册表回写);
/// 非 indexMode space -> 现有 string 字典 l10n-bin-split 逻辑(限定该 space 表);
/// checksum:普通表 checksumconfig.bytes 在输出根目录;每 space 在自己 outputDir 下输出含 per-language 行的 checksumconfig.bytes。
/// </summary>
[DataExporter("omnibus-baseline")]
public class OmnibusBaselineExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var spaces = ctx.L10NSpaces;
        var spaceTableNames = new HashSet<string>(spaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
        bool isSpaceTable(DefTable t) => spaceTableNames.Contains(t.Name) || spaceTableNames.Contains(t.FullName);

        // 1. 普通表(现有 baseline-with-sidecar,排除 space 表)
        var tableExporter = new BaselineWithSidecarExporter { TableFilter = isSpaceTable };
        tableExporter.Handle(ctx, dataTarget, manifest);

        foreach (var space in spaces)
        {
            if (space.IndexMode)
            {
                // 2. 数组格式 + sidecar v2 + checksum(Task 8/9 产物)
                L10NBinarySplitDataExporter.ExportL10NArrayPerLanguage(ctx, space, manifest);
                WriteIndexSpaceSidecarAndChecksum(ctx, dataTarget, space, manifest);
            }
            else
            {
                // 3. 旧 string 字典(限定 space 表)——通过 TableFilter 反向选择 + 现有 Handle
                //    实现方式:L10NBinarySplitDataExporter 加 ReverseFilter(只保留 space 表)后调用,
                //    其输出路径前缀改为 space.OutputDir(现有 BuildLanguageFilePath 的 {lang}/{file} 前加 outputDir)
                WriteLegacySpace(ctx, dataTarget, space, manifest);
            }
        }
    }

    private static void WriteIndexSpaceSidecar(GenerationContext ctx, IDataTarget dataTarget, Luban.L10N.L10NSpace space, OutputFileManifest manifest)
    {
        // sidecar v2:KeyEntries=注册表;Keys=活 key 紧凑快照;Languages[lang].Hashes 与 Keys 对齐;
        // Tables 段照抄 L10NBaselineWithSidecarExporter(space 表)。
        // checksum:space 语言行注入 ChecksumConfig 记录,dataTarget.ExportTable 输出到 space.OutputDir。
        // 具体实现抽取 L10NBaselineWithSidecarExporter 的私有逻辑复用(把 WriteL10NSidecar 改造为
        // 接受 space 参数的内部静态方法,旧路径传"合成的单 space")。
    }

    private static void WriteLegacySpace(GenerationContext ctx, IDataTarget dataTarget, Luban.L10N.L10NSpace space, OutputFileManifest manifest)
    {
        // 复用 L10NBinarySplitDataExporter 逐表/合并导出,输出根换成 space.OutputDir;
        // 表集合限定 MatchTables(ctx, space)。
        // sidecar:完整保留现有 l10n-baseline-with-sidecar 行为(写 space.sidecar),
        // 与合并前 langServer 基准产物零差异。
    }
}
```

> **实现方式说明(执行者必读)**:上面两个私有方法的注释即需求。落地时优先**重构现有逻辑为可复用静态方法**而不是复制:
> 1. `L10NBaselineWithSidecarExporter.WriteL10NSidecar` 改为 `internal static void WriteSpaceSidecar(GenerationContext ctx, L10NSpace space, OutputFileManifest manifest)`,旧 `Handle` 在未配 spaces 时构造一个合成 space(由旧单值选项填充)调用它——保证旧路径字节级行为不变。
> 2. checksum 语言行注入(`GenerationContext.AddL10NLanguageChecksumRecords`)与输出(`ExportChecksumTable`)按 space 拆分:注入方法加 space 参数(行 TableName=语言名,每个 space 的 checksumconfig 只含本 space 行);普通表行照旧进根目录 checksumconfig。
> 3. `GetL10NLangStamps` 的 per-language ContentHash 按数组序列化计算(`ComputePerLanguageFileMd5` 改为接受 space,序列化用 `SerializeLanguageArray`)。**保持一个总原则:未配置 spaces 时这些函数走旧分支,旧行为不变。**

- [ ] **Step 3: mini conf 冒烟**

```bash
cd tests/l10n-miniconf && rm -rf out_omnibus l10n.json && dotnet ../../src/Luban/bin/Debug/net8.0/Luban.dll --conf conf.json -t client -c cs-bin -d bin -f -x bin.outputDataDir=out_omnibus -x outputCodeDir=out_code -x dataExporter=omnibus-baseline -x l10n.spaces=main -x l10n.main.tables=LanguageText -x l10n.main.indexMode=true -x l10n.main.languages=zh_CN,en_US -x l10n.main.sidecar=l10n.json -x l10n.main.outputDir=language -x incremental.sidecarPath=l10n.json
```

Expected:
- `out_omnibus/language/zh_CN/languageconfig.bytes` 存在,字节 = `03 "你好" ""? ` 不——mini 数据无墓碑:`{count=2}{"你好"}{"再见"}`(可用 xxd/`Format-Hex` 检查);
- `out_omnibus/tbitem.bytes` 存在(nameId 为 varint 下标);
- `l10n.json` 含 `"KeyEntries"`;
- `out_omnibus/language/checksumconfig.bytes` 与根目录 `checksumconfig.bytes` 都存在;
- `out_code` 中 `Item.cs` 的 nameId 为 int。

- [ ] **Step 4: 幂等冒烟**

重复执行 Step 3 命令两次(不删 l10n.json),diff 两次 `out_omnibus` 全部 `.bytes` 与 `l10n.json` 的 KeyEntries:

```bash
# 第二次输出到 out_omnibus2 后:
diff -r out_omnibus out_omnibus2 && echo IDENTICAL
```

Expected: IDENTICAL(时间戳类 stamp 字段可能不同——sidecar 的 Stamp 是 unix 秒,两次连跑内容相同会被 stamp gating 沿用旧值,故应当一致;若不一致,检查 ContentHash gating 是否按数组格式计算)。

- [ ] **Step 5: Commit**

```bash
git add src/Luban.DataTarget.Builtin src/Luban.Core
git commit -m "feat: omnibus-baseline 组合导出器(普通表+多space语言+分目录checksum)"
```

---

### Task 11: omnibus-incremental + per-space stamp

**Files:**
- Modify: `src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs`(加 TableFilter 钩子)
- Create: `src/Luban.DataTarget.Builtin/Incremental/OmnibusIncrementalExporter.cs`

**Interfaces:**
- Produces: `[DataExporter("omnibus-incremental")]` —— 普通表 DLP1(排除 space 表)+ indexMode space LLP2 + 注册表回写 + 合并 `_delta.manifest`(普通表条目 + 语言条目,语言条目 Table=语言名)

- [ ] **Step 1: IncrementalDataExporter 加 TableFilter**

同 Task 10 Step 1 的方式。

- [ ] **Step 2: OmnibusIncrementalExporter**

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Luban.DataTarget;
using Luban.Defs;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 组合增量导出器:普通表 DLP1(排除 space 表)+ indexMode space LLP2(全下标)+ 注册表回写。
/// 产出单份 _delta.manifest(普通表条目 + 语言条目混排,语言条目 Table=语言名)。
/// </summary>
[DataExporter("omnibus-incremental")]
public class OmnibusIncrementalExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var spaces = ctx.L10NSpaces;
        var spaceTableNames = new HashSet<string>(spaces.SelectMany(s => s.Tables), StringComparer.Ordinal);
        bool isSpaceTable(DefTable t) => spaceTableNames.Contains(t.Name) || spaceTableNames.Contains(t.FullName);

        // 1. 普通表增量(现有导出器,排除 space 表)
        var tableExporter = new IncrementalDataExporter { TableFilter = isSpaceTable };
        tableExporter.Handle(ctx, dataTarget, manifest);

        // 2. indexMode space LLP2(Task 9 的 HandleIndexMode 抽为可复用内部静态方法后在此调用;
        //    其内部已含注册表回写)
        foreach (var space in spaces.Where(s => s.IndexMode))
        {
            IncrementalL10NDataExporter.HandleSpace(ctx, space, manifest);
        }

        // 3. manifest 合并:普通表导出器已写 _delta.manifest 的话,语言条目并入同一 manifest
        //    (实现时把两个导出器的 manifest 条目收集改为返回 List<DeltaManifestEntry>,
        //    Omnibus 统一写一份 _delta.manifest;单独使用旧导出器时行为不变)
    }
}
```

> 执行者注意:第 2/3 点需要把 Task 9 里 `HandleIndexMode` 的逻辑抽为 `IncrementalL10NDataExporter` 的 `internal static void HandleSpace(GenerationContext, L10NSpace, OutputFileManifest, List<DeltaManifestEntry>)`,旧入口调用它保持兼容。manifest 合并方式:给两个现有导出器各加一个"不直接写 manifest 文件,返回条目"的路径,或 Omnibus 里后处理——选前者,改动清晰。

- [ ] **Step 3: mini conf 增量冒烟**

基于 Task 10 的基准产物(`l10n.json` 已存在):

1. 修改 `data/lang.csv`:把 key1 的 en_US 改为 `Hi`;文件末尾加一行 `key3,三,Three`;
2. 修改 `data/item.csv`:加一行 `,3,key3`;

```bash
cd tests/l10n-miniconf && dotnet ../../src/Luban/bin/Debug/net8.0/Luban.dll --conf conf.json -t client -d bin -f --validationFailAsError -x bin.outputDataDir=out_delta -x dataExporter=omnibus-incremental -x l10n.spaces=main -x l10n.main.tables=LanguageText -x l10n.main.indexMode=true -x l10n.main.languages=zh_CN,en_US -x l10n.main.sidecar=l10n.json -x l10n.main.outputDir=language -x incremental.sidecarPath=l10n.json
```

Expected:
- `out_delta/language/en_US/languageconfig.patch.bytes`:upsert 2 条(key1 下标 0、key3 下标 2);zh_CN patch 只 upsert key3;
- `out_delta/tbitem.patch.bytes`(或 DLP1 命名)含新增行 id=3;
- `_delta.manifest` 同时含表条目与 zh_CN/en_US 语言条目;
- **重跑一次同命令**:en_US patch 仅剩 key1(第二次 diff 基于更新后的注册表与上次数值——注意 diff 基准是"基准快照",所以重跑仍会输出与第一次相同的 patch(相对基准累计);真正要断言的是 `l10n.json` 的 KeyEntries 稳定:`key3` 两次都在下标 2);
- 手工解析 patch 字节验证布局(用 ByteBuf 测试或临时脚本)。

- [ ] **Step 4: Commit**

```bash
git add src/Luban.DataTarget.Builtin/Incremental
git commit -m "feat: omnibus-incremental 组合增量导出器(DLP1+LLP2+注册表回写+合并manifest)"
```

---

### Task 12: cs-l10n-language 多 space + 模板重写

**Files:**
- Modify: `src/Luban.Core/L10NKeyInfo.cs`(加 Index)
- Modify: `src/Luban.Core/GenerationContext.cs`(EnumerateL10NKeys 关联注册表)
- Modify: `src/Luban.CSharp/CodeTarget/CsharpL10NLanguageCodeTarget.cs`(多 space)
- Modify: `src/Luban.CSharp/Templates/cs-l10n-language/language.sbn`(纯名+懒加载+index)

**Interfaces:**
- Produces: `L10NKeyInfo(object Key, string FieldName, string Key2, int Index)`;一个 run 输出每 space 一个 `{className}.cs`;模板字段 `public static string {{key}} => Get({{index}});`

- [ ] **Step 1: L10NKeyInfo 加 Index**

```csharp
public record L10NKeyInfo(object Key, string FieldName, string Key2, int Index = -1);
```

`EnumerateL10NKeys` 收尾处(现有 `result.Add(new L10NKeyInfo(key.Item1, fieldName, key.Item2))`):调用方(CsharpL10NLanguageCodeTarget)在拿到 keys 后按 space 注册表回填 Index(不在 Enumerate 内部做,保持其通用):

```csharp
foreach (var k in keys)
{
    if (space.KeyIndex.TryGetIndex((string)k.Key, out var idx))
    {
        k.Index = idx; // record 是不可变——见 Step 2 说明,改为可变 class 或用 with 拷贝
    }
    else
    {
        throw new Exception($"[cs-l10n-language] space '{space.Name}' 的 is_code key '{k.Key}' 不在注册表中(数据与注册表不同步,疑似 bug)");
    }
}
```

> record 的 `with` 表达式生成新对象,列表要重建。执行时二选一:改 `L10NKeyInfo` 为普通 class(有 init setter),或 `keys = keys.Select(k => reg.TryGetIndex(...) ? k with { Index = i } : throw).ToList()`。选 class(模板只读)。

- [ ] **Step 2: CsharpL10NLanguageCodeTarget 多 space**

`Handle` 改造(保留旧单值路径):

```csharp
public override void Handle(GenerationContext ctx, OutputFileManifest manifest)
{
    ctx.LoadDatas();

    if (ctx.L10NSpaces.Count > 0)
    {
        foreach (var space in ctx.L10NSpaces)
        {
            RenderSpace(ctx, manifest, space,
                EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.className", false, DefaultClassName(space)),
                EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.keyFlag", false, null),
                EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.keyTable", false, null));
        }
        return;
    }
    // 旧单值路径:现有 GetCodeL10NKeys + 单次渲染(不动)
    ...
}

private static string DefaultClassName(Luban.L10N.L10NSpace space) =>
    space.Name == "main" ? "LanguageConfig" : $"Language{Cap(space.Name)}Config";

private void RenderSpace(GenerationContext ctx, OutputFileManifest manifest, Luban.L10N.L10NSpace space,
    string className, string keyFlag, string keyTable)
{
    // 表集合:keyTable 选项 > space.Tables;flag 过滤沿用 GetL10NKeyInfos(matched, keyFlag)
    // keys 的 Key2(desc) 用 space.KeyFieldDesc
    // Index 按 Step 1 回填
    // 渲染模板(现有 CreateTemplateContext/CreateOutputFile 复用),输出 {className}.cs
}
```

> 选项名(spec §4.6):`cs-l10n-language.spaces` 不需要——直接跟随 `ctx.L10NSpaces`(数据管线是唯一事实源,代码目标不再单独配置 spaces 名单,避免两处不一致;className/keyFlag/keyTable 仍按 `cs-l10n-language.space.{name}.*` 覆盖默认值)。默认 className 规则:main→LanguageConfig,其他→Language{Name}Config。

- [ ] **Step 3: 模板重写**

`language.sbn` 整体替换:

```
{{ if __namespace && __namespace != "" -}}
namespace {{ __namespace }}
{
{{ end -}}

public class {{ __class_name }}
{
{{ for k in __keys -}}
{{~ $raw_desc = k.key2 != null ? k.key2 : "" ~}}
{{~ $desc = $raw_desc | string.replace "\r\n" "\n" | string.replace "\r" "\n" ~}}
{{~ $desc_lines = $desc | string.split "\n" ~}}
    /// <summary>
    {{ for line in $desc_lines -}}
    {{~ $line_trim = line | string.strip ~}}
    /// {{ $line_trim }}
    {{ end -}}
    /// </summary>
    public static string {{ k.key }} => Get({{ k.index }});

{{ end -}}
    public static string Get(int index)
    {
        return (uint)index < (uint)dataArrRef.Length ? dataArrRef[index] : string.Empty;
    }
}

{{ if __namespace && __namespace != "" -}}
}
{{ end -}}
```

> `dataArrRef` 由客户端手写 partial 提供(Plan 2 Task 4);生成类与手写 partial 同名同 namespace(`Table`,由 topModule 控制)。模板里 `Get` 生成在生成类中,手写 partial 只提供 `dataArrRef` 与构造/ApplyDelta——**与 Plan 2 Task 4 的手写文件约定一致,两份计划以此处为准**。删除现有模板的 `__key_type` 用法;`CsharpL10NLanguageCodeTarget` 里对应传参一并清理。

- [ ] **Step 4: mini conf 冒烟**

Task 10 Step 3 命令加 `-c cs-l10n-language -x cs-l10n-language.space.main.className=LanguageConfig`,检查 `out_code/LanguageConfig.cs`:

```csharp
public static string key1 => Get(0);
public static string key2 => Get(1);
```

且带 key2(desc)的 XML 注释。

- [ ] **Step 5: Commit**

```bash
git add src/Luban.Core/L10NKeyInfo.cs src/Luban.Core/GenerationContext.cs src/Luban.CSharp
git commit -m "feat: cs-l10n-language 多 space 输出 + 静态访问器烘焙下标(纯名+懒加载)"
```

---

### Task 13: 集成验证(幂等/追加/墓碑/复活全链)

**Files:**
- Create: `tests/l10n-miniconf/VERIFY.md`(验证记录)

**Interfaces:** Consumes: Task 1-12 全部。Produces: spec §9 的 1/2/3/6 项证据(mini conf 范围)。

- [ ] **Step 1: 幂等**

Task 10 Step 4 已覆盖,记录结果到 VERIFY.md。

- [ ] **Step 2: 追加稳定 + 交错追加**

1. 基准(lang.csv: key1,key2)→ 记录 KeyEntries;
2. 增量:加 key9 → 跑 omnibus-incremental → KeyEntries: key9 下标 2;
3. 再加 key0(Ordinal 靠前!)→ 再跑增量 → 断言:key9 仍下标 2,key0 下标 3(**交错追加不漂移**,spec §3.2 不变量);
4. 两步 patch 文件都存在且可解析。

- [ ] **Step 3: 墓碑 + 复活**

1. 删 key1 → 增量 → patch 含 delete(下标 0);KeyEntries key1 Deleted=true;
2. 恢复 key1 → 增量 → patch 含 upsert(下标 0);KeyEntries key1 Deleted=false;
3. 基准重跑 → 数组 bin 中 key1 槽位恢复有值,count 不减。

- [ ] **Step 4: 未知 key 报错**

item.csv 某行 nameId 填 `not_exist` → 跑基准 → 预期硬错误,信息含表名/字段/key;修回。

- [ ] **Step 5: 空 text 字段**

item.csv 某行 nameId 留空 → 基准 → WARN 日志 + 导出 varint(-1)(`0xFF` 前缀字节);bin 可解析。

- [ ] **Step 6: 记录 + Commit**

`VERIFY.md` 记录以上每步的实际命令输出摘要与断言结果。

```bash
git add tests/l10n-miniconf/VERIFY.md
git commit -m "test: l10n int 下标 mini conf 全链验证记录"
```

---

## Self-Review 结论

- **Spec 覆盖**:spec §3(注册表:Task 1/2/4/9)、§4.1(Task 3)、§4.2(Task 4)、§4.3(Task 8)、§4.4(Task 9/11)、§4.5(Task 10/11)、§4.6(Task 12)、§4.7(Task 5/6/7)、§4.8(不改项:未动)——全覆盖。§5~§7 归 Plan 2。
- **占位符**:Task 10/11/12 含"执行者注意"式指导(重构方向明确、接口签名固定),非 TBD;实现者据此有唯一落点。
- **类型一致性**:`L10NSpace`/`L10NKeyIndex`/`KeyEntry`/`KeyIndexAllocator.Allocate`/`SerializeLanguageArray`/`MagicL10N2`/`L10NKeyInfo.Index` 在各任务引用处签名一致(已交叉检查)。
