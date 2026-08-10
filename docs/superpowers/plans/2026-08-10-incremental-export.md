# 增量导表实现计划（Incremental Export）

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 给 Luban 增加"基准 + 增量"双模式导表：基准导出带上结构签名 SignatureId 并写 sidecar；增量导出读 sidecar 做行级 diff（普通表按主键、L10N 按 (语言,文本 key)），结构变化则整批中止；产出隔离在 `Output/delta/` 下且每次全清。

**Architecture:** 不新增 Pipeline，复用 `DefaultPipeline`。三块新代码落在既有扩展点：(1) SignatureId 计算（挂进 `CalculateTableChecksums`）；(2) 基准 sidecar 写入（新 `DataExporter` 包装 `tag-split`/`l10n-bin-split`）；(3) 增量导出器（新 `[DataExporter]`，读 sidecar + diff + 出 patch）。行字节统一用 `BinaryDataVisitor`（按目标 group 过滤，复用现有反序列化）。

**Tech Stack:** C# (.NET 8), NLog, Scriban, System.Text.Json, System.Security.Cryptography.MD5。

## Global Constraints

- **C# 约定**：PascalCase 类/方法/属性；`_camelCase` 私有字段；`camelCase` 局部/参数；`I` 接口前缀；4 空格缩进；Allman 大括号；传统 `namespace { }` 块；`Nullable disable`；文件头 MIT 许可（见 `.cursor/rules/normal.mdc`）；日志 `private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();`。
- **无测试套件**：本仓库无单元测试工程。每个任务的"验证"= 构建 + 跑 pipeline + 检查产物。验证命令模板（下称 **VERIFY**）：
  ```
  cd E:\Projects\luban\src ; dotnet build Luban.sln
  dotnet run --project E:/Projects/luban/src/Luban -- -t client -c cs-bin -d bin -f --validationFailAsError ^
    --conf D:/work/slg2/common/config/Tools/game.conf ^
    -x cs-bin.outputCodeDir=E:/Projects/luban/_verify/code ^
    -x bin.outputDataDir=E:/Projects/luban/_verify/data ^
    -x dataExporter=<本任务用的 exporter>
  ```
  （`_verify/` 是验证用临时目录，不入库；每任务可清空重用。）
- **SignatureId 契约**：大写 hex MD5；遍历 `DefBean.Fields` 全字段 **不调 `NeedExport`**（c/s 同值）；触发：加/删/改字段、改类型/可空、改枚举项、改继承、改 index/mode、改名；不触发：只改数据值。
- **`GetTypeId` 不动**：它是多态判别符（`DefBean.Id` 名字 hash），与 SignatureId 无关。本计划不碰 `ITypeId`/`BeanBase`/`DefBean.Id`/`BinaryDataVisitor` 写 id 路径/任何语言模板。
- **范围边界**：只改 Luban 导出工具 + 格式契约。**不改 proto、不写客户端/服务端运行时代码**（第二期）。
- **目录契约**：`Output/{client,server,delta/{client,server},_sidecar}`；基准写 `Output/client`、`Output/server`；增量写 `Output/delta/client`、`Output/delta/server`；sidecar 在 `Output/_sidecar`（工具内部，不上传）。
- **增量每次全清**：增量导出开工前脚本清空整个 `Output/delta/`，绝不 append。
- **PostBuild 陷阱**：`src/Luban/Luban.csproj:91-93` 有 PostBuild xcopy 到 `D:\work\slg\common\config\Tools\Luban`（本机存在能过；CI 会挂）。本计划不动它，但验证用 `dotnet run --project` 直接跑源码，绕过部署。

## File Structure

**新建（Luban.Core）：**
- `src/Luban.Core/TypeVisitors/StructureSignature.cs` — SignatureId 计算（静态类，递归 bean/enum/容器 + 表 index/mode -> MD5 hex）。
- `src/Luban.Core/Incremental/SidecarModels.cs` — sidecar JSON POCO 模型（普通表 + L10N）。
- `src/Luban.Core/Incremental/BaselineSidecar.cs` — sidecar 读写（System.Text.Json）。

**新建（Luban.DataTarget.Builtin）：**
- `src/Luban.DataTarget.Builtin/Incremental/BaselineWithSidecarExporter.cs` — `[DataExporter("baseline-with-sidecar")]`，包装 `TagSplitDataExporter` + 写普通表 sidecar。
- `src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs` — `[DataExporter("incremental")]`，读 sidecar + 结构 gate + 行 diff + 出 DLP1 patch + manifest。
- `src/Luban.DataTarget.Builtin/Incremental/PatchFormat.cs` — DLP1/LLP1 magic 常量 + 写 patch 字节。
- `src/Luban.DataTarget.Builtin/Incremental/L10NBaselineWithSidecarExporter.cs` — `[DataExporter("l10n-baseline-with-sidecar")]`，包装 `L10NBinarySplitDataExporter` + 写 L10N sidecar + `_l10n.checksum.bytes`。
- `src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs` — `[DataExporter("incremental-l10n-bin-split")]`，读 L10N sidecar + per-语言 diff + 出 LLP1 patch。

**修改：**
- `src/Luban.Core/Defs/DefTable.cs` — 加 `SignatureId` 属性。
- `src/Luban.Core/Checksum/ChecksumTableBuilder.cs` — `ChecksumInfo` 加 `SignatureId` 字段 + 记录。
- `src/Luban.Core/GenerationContext.cs` — `CalculateTableChecksums` 同时算 SignatureId。
- `src/Luban.Core/BuiltinOptionNames.cs` — 加增量相关 option 名。

**新建（slg2 配置工程，验证/交付）：**
- `D:\work\slg2\common\config\增量导出.bat` / `.sh` — 增量脚本（清 delta/ + client 增量 + server 全量 + L10N 增量）。

---

### Task 1: SignatureId 计算 + 接入 ChecksumInfo

**Files:**
- Create: `src/Luban.Core/TypeVisitors/StructureSignature.cs`
- Modify: `src/Luban.Core/Defs/DefTable.cs`（加属性）
- Modify: `src/Luban.Core/Checksum/ChecksumTableBuilder.cs:51-75,118-150`（加字段 + 记录）
- Modify: `src/Luban.Core/GenerationContext.cs:168-213`（算 SignatureId）

**Interfaces:**
- Produces: `StructureSignature.ComputeForTable(DefTable) -> string`（大写 hex MD5）；`DefTable.SignatureId` 属性（string）；`ChecksumInfo` bean 多一个 `SignatureId` 字段（第 3 字段）。

- [ ] **Step 1: 给 DefTable 加 SignatureId 属性**

`src/Luban.Core/Defs/DefTable.cs`，在 `public string Checksum` 附近（约 `:113`）加：
```csharp
public string SignatureId { get; set; } = "";
```

- [ ] **Step 2: 创建 StructureSignature.cs**

Create `src/Luban.Core/TypeVisitors/StructureSignature.cs`（文件头照抄同目录其它文件的 MIT 头）：
```csharp
using System.Text;
using Luban.Defs;
using Luban.Types;
using Luban.Utils;

namespace Luban.TypeVisitors;

/// <summary>
/// 计算表的结构签名 SignatureId（大写 hex MD5）。
/// 全字段、不调 NeedExport -> c/s 同值（与 BinaryChecksumVisitor 同原则）。
/// 与 GetTypeId 无关（GetTypeId 是名字 hash、多态判别；本类不碰它）。
/// 遍历逻辑对齐 DeepCompareTypeDefine，但输出 hash 而非 bool。
/// </summary>
public static class StructureSignature
{
    public static string ComputeForTable(DefTable table)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<DefBean>();
        sb.Append("T|").Append(table.FullName).Append('|').Append(table.Mode).Append('|').Append(table.Index ?? "").Append('\n');
        if (table.ValueTType is TBean tb)
        {
            AppendBean(sb, tb.DefBean, visited);
        }
        return FileUtil.CalcMD5(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void AppendBean(StringBuilder sb, DefBean bean, HashSet<DefBean> visited)
    {
        if (!visited.Add(bean)) return; // 环保护
        sb.Append("B|").Append(bean.FullName).Append('|').Append(bean.Parent ?? "")
          .Append('|').Append(bean.Alias ?? "").Append('|').Append(bean.IsMultiRow)
          .Append('|').Append(bean.Sep ?? "").Append('\n');
        sb.Append("FC|").Append(bean.Fields.Count).Append('\n');
        for (int i = 0; i < bean.Fields.Count; i++)
        {
            var f = bean.Fields[i];
            sb.Append("F|").Append(i).Append('|').Append(f.Name).Append('|').Append(f.CType.IsNullable).Append('|');
            AppendType(sb, f.CType, visited);
            sb.Append('\n');
        }
        if (bean.ParentDefType != null) AppendBean(sb, (DefBean)bean.ParentDefType, visited);
        if (bean.Children != null)
        {
            sb.Append("CC|").Append(bean.Children.Count).Append('\n');
            foreach (var c in bean.Children) AppendBean(sb, (DefBean)c, visited);
        }
    }

    private static void AppendType(StringBuilder sb, TType t, HashSet<DefBean> visited)
    {
        switch (t)
        {
            case TBean b: sb.Append("Bean:"); AppendBean(sb, b.DefBean, visited); break;
            case TEnum e:
                sb.Append("Enum:").Append(e.DefEnum.FullName).Append('|').Append(e.DefEnum.IsFlags)
                  .Append('|').Append(e.DefEnum.IsUniqueItemId).Append('|').Append(e.DefEnum.Items.Count);
                foreach (var it in e.DefEnum.Items) sb.Append('|').Append(it.Name).Append('=').Append(it.Value).Append(':').Append(it.Alias ?? "");
                break;
            case TArray a: sb.Append("Array:"); AppendType(sb, a.ElementType, visited); break;
            case TList l: sb.Append("List:"); AppendType(sb, l.ElementType, visited); break;
            case TSet s: sb.Append("Set:"); AppendType(sb, s.ElementType, visited); break;
            case TMap m: sb.Append("Map:"); AppendType(sb, m.KeyType, visited); AppendType(sb, m.ValueType, visited); break;
            default: sb.Append("P:").Append(t.GetType().Name); break; // TBool/TInt/TString/TDateTime/TDay...
        }
    }
}
```

- [ ] **Step 3: ChecksumInfo bean 加 SignatureId 字段**

`src/Luban.Core/Checksum/ChecksumTableBuilder.cs`，在 `Fields = new List<RawField>` 列表里（约 `:58-74`）`Checksum` 字段后追加第 3 字段：
```csharp
new RawField
{
    Name = "SignatureId",
    Type = "string",
    Comment = "结构签名（MD5，全字段，前后端一致）",
    Groups = new List<string>()
}
```

- [ ] **Step 4: CreateChecksumRecords 填 SignatureId**

同文件 `CreateChecksumRecords`（约 `:118-150`），把字段获取从 2 个改 3 个，记录 DBean 加第 3 字段：
```csharp
var tableNameField = defBean.HierarchyFields[0];  // TableName
var checksumField = defBean.HierarchyFields[1];    // Checksum
var signatureIdField = defBean.HierarchyFields[2]; // SignatureId
```
循环里构造 `fields`：
```csharp
var fields = new List<DType>
{
    DString.ValueOf(tableNameField.CType, table.Name),
    DString.ValueOf(checksumField.CType, table.Checksum),
    DString.ValueOf(signatureIdField.CType, table.SignatureId ?? "")
};
```

- [ ] **Step 5: CalculateTableChecksums 算 SignatureId**

`src/Luban.Core/GenerationContext.cs:168-213`，在 `table.Checksum = checksum;`（约 `:197`）后加：
```csharp
table.SignatureId = TypeVisitors.StructureSignature.ComputeForTable(table);
```
空表分支（约 `:179` `table.Checksum = "";`）也加 `table.SignatureId = TypeVisitors.StructureSignature.ComputeForTable(table);`（空表也有结构）。

- [ ] **Step 6: 构建 + 跑基准 + 验证 checksumconfig 含 SignatureId**

Run VERIFY（`dataExporter=tag-split`，本任务先不换 exporter，验证 SignatureId 已进 checksum）。
Expected: 构建通过；`_verify/data/basic/checksumconfig.bytes` 生成。
检查：用 `Luban.dll` 反序列化或写个一次性脚本读 `checksumconfig.bytes`，确认每条记录有 3 个字段（TableName, Checksum, SignatureId），SignatureId 是 32 位大写 hex。
最简检查（PowerShell，把 bytes 当 bin 手读太繁，改用 JSON 输出验证）：
```
dotnet run --project E:/Projects/luban/src/Luban -- -t client -c cs-bin -d json -f --validationFailAsError --conf D:/work/slg2/common/config/Tools/game.conf -x cs-bin.outputCodeDir=E:/Projects/luban/_verify/code -x json.outputDataDir=E:/Projects/luban/_verify/json -x dataExporter=tag-split
```
然后读 `_verify/json/basic/checksumconfig.json`，确认每行对象含 `"SignatureId": "..."`（32 hex）。

- [ ] **Step 7: 验证 c/s 同值**

再跑一次 server target（java-json）：
```
dotnet run --project E:/Projects/luban/src/Luban -- -t server -c java-json -d json -f --validationFailAsError --conf D:/work/slg2/common/config/Tools/game.conf -x outputCodeDir=E:/Projects/luban/_verify/svrcode -x outputDataDir=E:/Projects/luban/_verify/svrjson -x java-json.codePackage=cfg
```
读 `_verify/svrjson/checksumconfig.json`，对同一张表（如 Building）其 SignatureId 应与 client 的**完全相同**。

- [ ] **Step 8: 验证结构变化触发 SignatureId 变**

随便给某张表（如 `Building`）的 bean 加一个字段（在 `Defines/` 对应 xml 或源 CSV 列），重跑 client，对比前后 `Building` 的 SignatureId 不同；改回去再跑，SignatureId 复原。验证后把改动还原。

- [ ] **Step 9: Commit**
```bash
git add src/Luban.Core/TypeVisitors/StructureSignature.cs src/Luban.Core/Defs/DefTable.cs src/Luban.Core/Checksum/ChecksumTableBuilder.cs src/Luban.Core/GenerationContext.cs
git commit -m "feat: ChecksumInfo 增加 SignatureId（全字段结构 MD5，前后端一致）"
```
（按 CLAUDE.md，提交前先更新 CHANGELOG.md 同一日期段，与代码同 commit。）

---

### Task 2: 基准 sidecar 写入（普通表）

**Files:**
- Create: `src/Luban.Core/Incremental/SidecarModels.cs`
- Create: `src/Luban.Core/Incremental/BaselineSidecar.cs`
- Create: `src/Luban.DataTarget.Builtin/Incremental/BaselineWithSidecarExporter.cs`
- Modify: `src/Luban.Core/BuiltinOptionNames.cs`（加 option 名）

**Interfaces:**
- Consumes: Task 1 的 `DefTable.SignatureId`；`BinaryDataVisitor.Ins`（行字节，在 `Luban.DataTarget.Builtin.Binary`）；`FileUtil.CalcMD5`。
- Produces: `[DataExporter("baseline-with-sidecar")]`；sidecar JSON 路径由 `-x incremental.sidecarPath=...` 配置；sidecar 结构见下。

- [ ] **Step 1: 加 option 名**

`src/Luban.Core/BuiltinOptionNames.cs` 末尾加：
```csharp
public const string IncrementalFamily = "incremental";
public const string IncrementalSidecarPath = "incremental.sidecarPath";
public const string IncrementalOutputDir = "incremental.outputDir";
```

- [ ] **Step 2: sidecar 模型**

Create `src/Luban.Core/Incremental/SidecarModels.cs`：
```csharp
using System.Collections.Generic;

namespace Luban.Incremental;

public class TableSidecarEntry
{
    public string SignatureId { get; set; } = "";
    public string Mode { get; set; } = "";
    public string PrimaryKeyIndex { get; set; } = "";
    public int RowCount { get; set; }
    public Dictionary<string, string> RowHashes { get; set; } = new();
}

public class BaselineSidecar
{
    public string Target { get; set; } = "";
    public Dictionary<string, TableSidecarEntry> Tables { get; set; } = new();
}
```

- [ ] **Step 3: sidecar 读写**

Create `src/Luban.Core/Incremental/BaselineSidecarIO.cs`：
```csharp
using System.IO;
using System.Text.Json;

namespace Luban.Incremental;

public static class BaselineSidecarIO
{
    private static readonly JsonSerializerOptions s_opt = new() { WriteIndented = true };

    public static BaselineSidecar Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BaselineSidecar>(json) ?? new BaselineSidecar();
    }

    public static void Save(string path, BaselineSidecar sidecar)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, JsonSerializer.Serialize(sidecar, s_opt));
    }
}
```

- [ ] **Step 4: BaselineWithSidecarExporter**

Create `src/Luban.DataTarget.Builtin/Incremental/BaselineWithSidecarExporter.cs`（文件头照抄 MIT）：
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Luban.Core.Incremental;   // 注意：Luban.DataTarget.Builtin 引用 Luban.Core
using Luban.DataExporter.Builtin.Binary;  // BinaryDataVisitor.Ins 所在
using Luban.DataTarget;
using Luban.Defs;
using Luban.Serialization;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

// 注意命名空间引用：Luban.Core 的 Incremental 命名空间在 Luban.Core 程序集。
// 若 Luban.Core 不被 Luban.DataTarget.Builtin 引用，改用下方"行 hash 计算本地化"方案。
// （Luban.DataTarget.Builtin 已引用 Luban.Core，见其 csproj，故可直接用。）

[DataExporter("baseline-with-sidecar")]
public class BaselineWithSidecarExporter : TagSplitDataExporter
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 先走正常 tag-split 导出（产 .bytes）
        base.Handle(ctx, dataTarget, manifest);

        // 2. 写 sidecar
        try
        {
            WriteSidecar(ctx, dataTarget);
        }
        catch (Exception e)
        {
            // sidecar 失败不应阻断基准导出
            Console.Error.WriteLine($"[baseline-with-sidecar] write sidecar failed: {e}");
        }
    }

    private void WriteSidecar(GenerationContext ctx, IDataTarget dataTarget)
    {
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path)) return; // 未配置则不写

        var sidecar = new BaselineSidecar { Target = ctx.TargetName };
        foreach (var table in ctx.Tables)
        {
            var records = ctx.GetTableExportDataList(table);
            if (records == null || records.Count == 0) continue;

            var entry = new TableSidecarEntry
            {
                SignatureId = table.SignatureId,
                Mode = table.Mode.ToString(),
                PrimaryKeyIndex = table.Index ?? "",
                RowCount = records.Count,
                RowHashes = new()
            };
            foreach (var rec in records)
            {
                var buf = new ByteBuf();
                rec.Data.Apply(BinaryDataVisitor.Ins, buf);  // 按 group 过滤的行字节
                entry.RowHashes[ExtractKey(table, rec)] = FileUtil.CalcMD5(buf.CopyData());
            }
            sidecar.Tables[table.FullName] = entry;
        }
        BaselineSidecarIO.Save(path, sidecar);
    }

    private static string ExtractKey(DefTable table, Record rec)
    {
        // 主键字段值 -> 字符串 key（用于 sidecar/diff 字典）
        if (table.IndexFieldIdIndex < 0 || table.IndexFieldIdIndex >= rec.Data.Fields.Count)
        {
            return rec.AutoIndex.ToString(); // 无主键（ONE/LIST）退化用序号
        }
        return rec.Data.Fields[table.IndexFieldIdIndex].ToString();
    }
}
```

> 注：`ctx.TargetName`、`ctx.Tables`、`ctx.GetTableExportDataList` 均为现有成员（见 `GenerationContext`）。`table.IndexFieldIdIndex` 见 `DefTable.cs:93-95`。`TagSplitDataExporter` 在 `Luban.DataExporter.Builtin` 命名空间（同项目）。

- [ ] **Step 5: 构建 + 跑基准 + 验证 sidecar**

Run VERIFY with `dataExporter=baseline-with-sidecar` + `-x incremental.sidecarPath=E:/Projects/luban/_verify/_sidecar/_baseline.client.sidecar.json`。
Expected: `_verify/data/basic/*.bytes` 正常产出（同 tag-split）；`_verify/_sidecar/_baseline.client.sidecar.json` 生成。
打开 JSON 确认：含 `target`、`tables` 字典；每表有 `signatureId`/`mode`/`primaryKeyIndex`/`rowCount`/`rowHashes`；`rowHashes` 条数 == 该表记录数。

- [ ] **Step 6: Commit**
```bash
git add src/Luban.Core/Incremental/ src/Luban.DataTarget.Builtin/Incremental/BaselineWithSidecarExporter.cs src/Luban.Core/BuiltinOptionNames.cs
git commit -m "feat: baseline-with-sidecar 导出器写基准 sidecar（per-table 主键->行hash）"
```
（+ CHANGELOG 同 commit。）

---

### Task 3: 增量导出器（普通表）

**Files:**
- Create: `src/Luban.DataTarget.Builtin/Incremental/PatchFormat.cs`
- Create: `src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs`

**Interfaces:**
- Consumes: Task 1 `DefTable.SignatureId` + `StructureSignature.ComputeForTable`；Task 2 sidecar 模型/IO + `BaselineSidecarIO.Load`；`BinaryDataVisitor.Ins`；`ByteBuf`。
- Produces: `[DataExporter("incremental")]`；产出 DLP1 patch 文件（`{table}.patch.bytes`）+ `_delta.manifest`；结构不一致时 throw 中止。

- [ ] **Step 1: PatchFormat 常量 + 写出**

Create `src/Luban.DataTarget.Builtin/Incremental/PatchFormat.cs`：
```csharp
using System.Text;
using Luban.Serialization;

namespace Luban.DataExporter.Builtin.Incremental;

public static class PatchFormat
{
    public const string MagicTable = "DLP1";   // Delta Patch v1（普通表）
    public const string MagicL10N = "LLP1";    // L10N Language Patch v1

    public static void WriteMagic(ByteBuf buf, string magic)
    {
        foreach (var c in magic) buf.WriteByte((byte)c);
    }
}
```

- [ ] **Step 2: IncrementalDataExporter**

Create `src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs`：
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Luban.Core.Incremental;
using Luban.DataExporter.Builtin.Binary;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Serialization;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

[DataExporter("incremental")]
public class IncrementalDataExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var sidecarPath = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(sidecarPath))
        {
            throw new InvalidOperationException("[incremental] 未配置 incremental.sidecarPath，无法 diff。请先跑基准导出。");
        }
        var baseline = BaselineSidecarIO.Load(sidecarPath);
        if (baseline.Target != ctx.TargetName)
        {
            throw new InvalidOperationException($"[incremental] sidecar target 不匹配：sidecar={baseline.Target}，当前={ctx.TargetName}");
        }

        // 第一遍：结构 gate（全表扫，收集所有不一致）
        var offenders = new List<(string Table, string Reason)>();
        foreach (var table in ctx.Tables)
        {
            var curSig = TypeVisitors.StructureSignature.ComputeForTable(table);
            if (!baseline.Tables.TryGetValue(table.FullName, out var baseEntry))
            {
                offenders.Add((table.FullName, $"基准中不存在（新增表，需新客户端代码）"));
                continue;
            }
            if (curSig != baseEntry.SignatureId)
            {
                offenders.Add((table.FullName, $"SignatureId 期望 {baseEntry.SignatureId} 实际 {curSig}（结构变化）"));
            }
        }
        foreach (var kv in baseline.Tables)
        {
            if (!ctx.Tables.Any(t => t.FullName == kv.Key))
            {
                offenders.Add((kv.Key, $"当前已移除（删除表）"));
            }
        }
        if (offenders.Count > 0)
        {
            var msg = new System.Text.StringBuilder();
            msg.AppendLine("[增量导出已终止] 检测到结构变化，无法在旧基准上叠加增量。请重新执行基准导出（会刷新 sidecar）。");
            foreach (var o in offenders) msg.AppendLine($"  - {o.Table} : {o.Reason}");
            msg.AppendLine("本次未产出任何 delta 文件。");
            throw new InvalidOperationException(msg.ToString());
        }

        // 第二遍：行 diff（仅结构全一致时）
        var changedTables = new List<DeltaManifestEntry>();
        foreach (var table in ctx.Tables)
        {
            var baseEntry = baseline.Tables[table.FullName];
            var records = ctx.GetTableExportDataList(table);
            var curHashes = new Dictionary<string, (string Hash, Record Rec)>();
            foreach (var rec in records)
            {
                var buf = new ByteBuf();
                rec.Data.Apply(BinaryDataVisitor.Ins, buf);
                curHashes[ExtractKey(table, rec)] = (FileUtil.CalcMD5(buf.CopyData()), rec);
            }

            var upserts = new List<Record>();
            var deletes = new List<string>();
            foreach (var kv in curHashes)
            {
                if (!baseEntry.RowHashes.TryGetValue(kv.Key, out var oldHash) || oldHash != kv.Value.Hash)
                    upserts.Add(kv.Value.Rec);
            }
            foreach (var kv in baseEntry.RowHashes)
            {
                if (!curHashes.ContainsKey(kv.Key)) deletes.Add(kv.Key);
            }

            if (upserts.Count == 0 && deletes.Count == 0) continue;

            var file = WritePatch(table, baseEntry.SignatureId, upserts, deletes);
            manifest.AddFile(file);
            changedTables.Add(new DeltaManifestEntry { Table = table.FullName, UpsertCount = upserts.Count, DeleteCount = deletes.Count, PatchFile = file.File });
        }

        // 写 manifest
        var man = new DeltaManifest { BaselineSignatureId = "", SidecarPath = sidecarPath, ChangedTables = changedTables };
        manifest.AddFile(new OutputFile { File = "_delta.manifest", Content = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(man, new JsonSerializerOptions { WriteIndented = true })) });
    }

    private static OutputFile WritePatch(DefTable table, string signatureId, List<Record> upserts, List<string> deletes)
    {
        var buf = new ByteBuf();
        PatchFormat.WriteMagic(buf, PatchFormat.MagicTable);
        buf.WriteString(signatureId);
        buf.WriteSize(upserts.Count);
        foreach (var rec in upserts) rec.Data.Apply(BinaryDataVisitor.Ins, buf);  // 行字节（与全表一致）
        buf.WriteSize(deletes.Count);
        foreach (var k in deletes) buf.WriteString(k);  // 主键字符串（与 sidecar 同编码）
        return new OutputFile { File = $"{table.OutputDataFile}.patch.bytes", Content = buf.CopyData() };
    }

    private static string ExtractKey(DefTable table, Record rec)
    {
        if (table.IndexFieldIdIndex < 0 || table.IndexFieldIdIndex >= rec.Data.Fields.Count)
            return rec.AutoIndex.ToString();
        return rec.Data.Fields[table.IndexFieldIdIndex].ToString();
    }
}

public class DeltaManifest
{
    public string BaselineSignatureId { get; set; } = "";
    public string SidecarPath { get; set; } = "";
    public List<DeltaManifestEntry> ChangedTables { get; set; } = new();
}
public class DeltaManifestEntry
{
    public string Table { get; set; } = "";
    public int UpsertCount { get; set; }
    public int DeleteCount { get; set; }
    public string PatchFile { get; set; } = "";
}
```

> 注：`rec.Data.Fields[index]` 是 `DType`，`.ToString()` 需给出可逆的 key 串。对 int/long/string 主键，`DInt.ToString()` 等返回值字符串，可作字典 key 且与 delete 段 `WriteString` 一致。若主键是组合/复杂类型，需在 `ExtractKey` 里特化（本工程组合索引如 `type+level`，需拼成 `type_level` 串）。

- [ ] **Step 3: 构建 + 准备 sidecar**

先跑基准（Task 2 的 VERIFY）生成 `_verify/_sidecar/_baseline.client.sidecar.json`，作为增量 diff 的基准。

- [ ] **Step 4: 改一条数据 + 跑增量 + 验证 patch**

手动改 `D:\work\slg2\common\config\Tables\` 下某表一条数据（如 Building 某行数值），跑：
```
dotnet run --project E:/Projects/luban/src/Luban -- -t client -c cs-bin -d bin -f --validationFailAsError --conf D:/work/slg2/common/config/Tools/game.conf -x cs-bin.outputCodeDir=E:/Projects/luban/_verify/code2 -x bin.outputDataDir=E:/Projects/luban/_verify/delta/client -x dataExporter=incremental -x incremental.sidecarPath=E:/Projects/luban/_verify/_sidecar/_baseline.client.sidecar.json
```
Expected: `_verify/delta/client/` 下产出变化的表的 `{table}.patch.bytes` + `_delta.manifest`；未变化的表无 patch。
检查 patch：前 4 字节 = `DLP1`；接着 signatureId 字符串；upsert 段、delete 段计数合理。

- [ ] **Step 5: 验证结构变化中止**

给某表加一个字段（改 bean 定义），跑 Step 4 的增量命令。
Expected: 命令报错退出（非 0），错误信息列该表 SignatureId 不一致，**不产出任何 patch/manifest**。改回去还原。

- [ ] **Step 6: 验证删除**

删一条数据，跑增量，确认该表 patch 的 delete 段含对应主键。还原。

- [ ] **Step 7: Commit**
```bash
git add src/Luban.DataTarget.Builtin/Incremental/PatchFormat.cs src/Luban.DataTarget.Builtin/Incremental/IncrementalDataExporter.cs
git commit -m "feat: incremental 导出器（行级 upsert+delete，结构变化整批中止）"
```
（+ CHANGELOG。）

---

### Task 4: L10N 基准 sidecar + checksum

**Files:**
- Create: `src/Luban.DataTarget.Builtin/Incremental/L10NBaselineWithSidecarExporter.cs`
- Modify: `src/Luban.Core/Incremental/SidecarModels.cs`（加 L10N 模型）

**Interfaces:**
- Consumes: `L10NBinarySplitDataExporter` 的拆语言逻辑（`ExportL10NMergedPerLanguage` 等，需抽为可复用 internal 或在新 exporter 里复刻）；`ctx.L10NLanguages`、`ctx.L10NTextKeyFieldName`。
- Produces: `[DataExporter("l10n-baseline-with-sidecar")]`；`language/_baseline.l10n.sidecar.json`（per-语言 key->MD5(value)）；`language/_l10n.checksum.bytes`（signatureId + per-lang MD5）。

- [ ] **Step 1: L10N sidecar 模型**

`src/Luban.Core/Incremental/SidecarModels.cs` 追加：
```csharp
public class L10NSidecar
{
    public string SignatureId { get; set; } = "";
    public Dictionary<string, LangSidecar> Languages { get; set; } = new();
}
public class LangSidecar
{
    public Dictionary<string, string> RowHashes { get; set; } = new(); // key -> MD5(value)
}
```

- [ ] **Step 2: 抽 L10N 拆语言逻辑为可复用**

`src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs`：把 `ExportL10NMergedPerLanguage`、`FindField`、`FindLanguageFields`、`IsValidKeyType`、`GetKeyValue`、`SerializeDictionaryToBinary`、`BuildLanguageFilePath` 从 `private static` 改为 `internal static`（同 assembly 可见），供新 exporter 复用。不改逻辑，仅改可见性。

- [ ] **Step 3: L10NBaselineWithSidecarExporter**

Create `src/Luban.DataTarget.Builtin/Incremental/L10NBaselineWithSidecarExporter.cs`：
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using Luban.Core.Incremental;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

[DataExporter("l10n-baseline-with-sidecar")]
public class L10NBaselineWithSidecarExporter : L10NBinarySplitDataExporter
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 正常 l10n-bin-split 导出（产 language/{lang}/languageconfig.bytes）
        base.Handle(ctx, dataTarget, manifest);

        // 2. 写 L10N sidecar + _l10n.checksum.bytes
        try { WriteL10NSidecar(ctx); } catch (Exception e) { Console.Error.WriteLine($"[l10n-baseline-with-sidecar] sidecar failed: {e}"); }
    }

    private void WriteL10NSidecar(GenerationContext ctx)
    {
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path)) return;

        var languages = ctx.L10NLanguages;
        var keyFieldName = ctx.L10NTextKeyFieldName;
        var tables = ctx.Tables;

        // 合并所有语言表的 (key,lang)->value（镜像 ExportL10NMergedPerLanguage 的合并）
        var perLang = new Dictionary<string, Dictionary<object, string>>(); // lang -> key->value
        foreach (var lang in languages) perLang[lang] = new();

        foreach (var table in tables)
        {
            if (table.ValueTType is not TBean tbean) continue;
            var bean = tbean.DefBean;
            var keyField = L10NBinarySplitDataExporter.FindField(bean, keyFieldName);
            if (keyField == null || !L10NBinarySplitDataExporter.IsValidKeyType(keyField.CType)) continue;
            var langFields = L10NBinarySplitDataExporter.FindLanguageFields(bean, languages);
            if (langFields.Count == 0) continue;

            foreach (var rec in ctx.GetTableExportDataList(table))
            {
                if (rec.Data is not DBean data) continue;
                var dKey = data.GetField(keyFieldName);
                var key = L10NBinarySplitDataExporter.GetKeyValue(dKey);
                if (key == null) continue;
                if (key is string s && string.IsNullOrEmpty(s)) continue;
                foreach (var lf in langFields)
                {
                    var val = (data.GetField(lf.Name) as DString)?.Value ?? "";
                    perLang[lf.Name][key] = val;
                }
            }
        }

        // SignatureId：取第一个语言表的 bean（全语言共享同一 Language bean）
        string sigId = "";
        foreach (var t in tables) { if (t.ValueTType is TBean tb) { sigId = TypeVisitors.StructureSignature.ComputeForTable(t); break; } }

        var sidecar = new L10NSidecar { SignatureId = sigId };
        var perLangMd5 = new Dictionary<string, string>();
        foreach (var (lang, map) in perLang)
        {
            var langEntry = new LangSidecar();
            var bufAll = new ByteBuf();
            bufAll.WriteSize(map.Count);
            foreach (var kv in map)
            {
                var kStr = kv.Key.ToString();
                var md5 = Utils.FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(kv.Value ?? ""));
                langEntry.RowHashes[kStr] = md5;
                // 同时重算整语言文件 MD5
                L10NBinarySplitDataExporter.WriteKey(bufAll, kv.Key, keyField?.CType); // 复用 WriteKey
                bufAll.WriteString(kv.Value ?? "");
            }
            perLangMd5[lang] = Utils.FileUtil.CalcMD5(bufAll.CopyData());
            sidecar.Languages[lang] = langEntry;
        }
        BaselineSidecarIO.Save(path, sidecar); // 注意：L10N 用单独文件，path 配 language/_baseline.l10n.sidecar.json

        // 写 _l10n.checksum.bytes（bin）：signatureId + langCount + per lang[name, md5]
        var csPath = EnvManager.Current.GetOptionOrDefault("", "incremental.l10nChecksumPath", true, "");
        if (!string.IsNullOrEmpty(csPath))
        {
            var cs = new ByteBuf();
            cs.WriteString(sigId);
            cs.WriteSize(perLangMd5.Count);
            foreach (var kv in perLangMd5) { cs.WriteString(kv.Key); cs.WriteString(kv.Value); }
            System.IO.File.WriteAllBytes(csPath, cs.CopyData());
        }
    }
}
```

> 注：`WriteKey` 在 L10NBinarySplitDataExporter 是 `private static`，Step 2 已改 `internal`。`ctx.L10NLanguages`/`ctx.L10NTextKeyFieldName` 见 `GenerationContext.cs:92-97`。

- [ ] **Step 4: 构建 + 跑 L10N 基准 + 验证**

```
dotnet run --project E:/Projects/luban/src/Luban -- -t all -c cs-l10n-language -d bin -f --validationFailAsError --conf D:/work/slg2/common/config/Tools/lang.conf -x cs-l10n-language.outputCodeDir=E:/Projects/luban/_verify/langcode -x bin.outputDataDir=E:/Projects/luban/_verify/client/language -x dataExporter=l10n-baseline-with-sidecar -x incremental.sidecarPath=E:/Projects/luban/_verify/_sidecar/language/_baseline.l10n.sidecar.json -x incremental.l10nChecksumPath=E:/Projects/luban/_verify/client/language/_l10n.checksum.bytes
```
Expected: `language/{lang}/languageconfig.bytes` 正常；`_sidecar/language/_baseline.l10n.sidecar.json` 生成（含 signatureId + 每语言 rowHashes）；`language/_l10n.checksum.bytes` 生成（前为 signatureId 字符串 + langCount + per-lang）。

- [ ] **Step 5: Commit**
```bash
git add src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs src/Luban.DataTarget.Builtin/Incremental/L10NBaselineWithSidecarExporter.cs src/Luban.Core/Incremental/SidecarModels.cs
git commit -m "feat: l10n-baseline-with-sidecar 导出器写 L10N sidecar + _l10n.checksum"
```
（+ CHANGELOG。）

---

### Task 5: L10N 增量导出器

**Files:**
- Create: `src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs`

**Interfaces:**
- Consumes: Task 4 的 L10N sidecar (`L10NSidecar`) + `BaselineSidecarIO.Load`（需泛型化或加 `LoadL10N`）；`ctx.L10NLanguages`/`L10NTextKeyFieldName`；L10N 拆语言逻辑（internal）。
- Produces: `[DataExporter("incremental-l10n-bin-split")]`；per-语言 `languageconfig.patch.bytes`（LLP1）+ `_l10n.delta.manifest`。

- [ ] **Step 1: BaselineSidecarIO 加 L10N 重载**

`src/Luban.Core/Incremental/BaselineSidecarIO.cs` 加：
```csharp
public static L10NSidecar LoadL10N(string path)
{
    var json = File.ReadAllText(path);
    return JsonSerializer.Deserialize<L10NSidecar>(json) ?? new L10NSidecar();
}
```

- [ ] **Step 2: IncrementalL10NDataExporter**

Create `src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs`：
```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Luban.Core.Incremental;
using Luban.DataTarget;
using Luban.Defs;
using Luban.Serialization;
using Luban.Types;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

[DataExporter("incremental-l10n-bin-split")]
public class IncrementalL10NDataExporter : DataExporterBase
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        var sidecarPath = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(sidecarPath))
            throw new InvalidOperationException("[incremental-l10n] 未配置 incremental.sidecarPath");
        var baseline = BaselineSidecarIO.LoadL10N(sidecarPath);

        // 结构 gate：当前 Language bean SignatureId vs baseline
        string curSig = "";
        foreach (var t in ctx.Tables) { if (t.ValueTType is TBean) { curSig = TypeVisitors.StructureSignature.ComputeForTable(t); break; } }
        if (curSig != baseline.SignatureId)
            throw new InvalidOperationException($"[增量导出已终止] L10N 结构变化：SignatureId 期望 {baseline.SignatureId} 实际 {curSig}。请重新执行基准导出。\n本次未产出任何 delta 文件。");

        var languages = ctx.L10NLanguages;
        var keyFieldName = ctx.L10NTextKeyFieldName;

        // 合并当前 (lang,key)->value
        var perLang = BuildCurrent(ctx, languages, keyFieldName);

        var changed = new List<DeltaManifestEntry>();
        foreach (var lang in languages)
        {
            var cur = perLang.GetValueOrDefault(lang) ?? new Dictionary<object, string>();
            var baseEntry = baseline.Languages.GetValueOrDefault(lang);
            var upserts = new List<KeyValuePair<object, string>>();
            var deletes = new List<string>();

            foreach (var kv in cur)
            {
                var kStr = kv.Key.ToString();
                var md5 = Utils.FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(kv.Value ?? ""));
                if (baseEntry == null || !baseEntry.RowHashes.TryGetValue(kStr, out var oldHash) || oldHash != md5)
                    upserts.Add(kv);
            }
            if (baseEntry != null)
            {
                foreach (var kv in baseEntry.RowHashes)
                    if (!cur.Keys.Any(o => o.ToString() == kv.Key)) deletes.Add(kv.Key);
            }

            if (upserts.Count == 0 && deletes.Count == 0) continue;

            var buf = new ByteBuf();
            PatchFormat.WriteMagic(buf, PatchFormat.MagicL10N);
            buf.WriteString(baseline.SignatureId);
            buf.WriteSize(upserts.Count);
            // key 类型需与 L10N 输出一致；本工程 key=string
            foreach (var kv in upserts) { buf.WriteString((string)kv.Key); buf.WriteString(kv.Value ?? ""); }
            buf.WriteSize(deletes.Count);
            foreach (var k in deletes) buf.WriteString(k);

            manifest.AddFile(new OutputFile { File = $"{lang}/languageconfig.patch.bytes", Content = buf.CopyData() });
            changed.Add(new DeltaManifestEntry { Table = lang, UpsertCount = upserts.Count, DeleteCount = deletes.Count, PatchFile = $"{lang}/languageconfig.patch.bytes" });
        }

        manifest.AddFile(new OutputFile { File = "_l10n.delta.manifest", Content = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new DeltaManifest { BaselineSignatureId = baseline.SignatureId, SidecarPath = sidecarPath, ChangedTables = changed }, new JsonSerializerOptions { WriteIndented = true })) });
    }

    private static Dictionary<string, Dictionary<object, string>> BuildCurrent(GenerationContext ctx, IReadOnlyList<string> languages, string keyFieldName)
    {
        var perLang = new Dictionary<string, Dictionary<object, string>>();
        foreach (var lang in languages) perLang[lang] = new();
        foreach (var table in ctx.Tables)
        {
            if (table.ValueTType is not TBean tbean) continue;
            var bean = tbean.DefBean;
            var keyField = L10NBinarySplitDataExporter.FindField(bean, keyFieldName);
            if (keyField == null || !L10NBinarySplitDataExporter.IsValidKeyType(keyField.CType)) continue;
            var langFields = L10NBinarySplitDataExporter.FindLanguageFields(bean, languages);
            if (langFields.Count == 0) continue;
            foreach (var rec in ctx.GetTableExportDataList(table))
            {
                if (rec.Data is not DBean data) continue;
                var key = L10NBinarySplitDataExporter.GetKeyValue(data.GetField(keyFieldName));
                if (key == null) continue;
                if (key is string s && string.IsNullOrEmpty(s)) continue;
                foreach (var lf in langFields)
                {
                    perLang[lf.Name][key] = (data.GetField(lf.Name) as DString)?.Value ?? "";
                }
            }
        }
        return perLang;
    }
}
```

- [ ] **Step 3: 构建 + 改一条文本 + 跑 L10N 增量 + 验证**

先跑 Task 4 的 L10N 基准生成 sidecar。然后改 `LanguageText.csv` 某个 key 的某语言文案，跑：
```
dotnet run --project E:/Projects/luban/src/Luban -- -t all -c cs-l10n-language -d bin -f --validationFailAsError --conf D:/work/slg2/common/config/Tools/lang.conf -x cs-l10n-language.outputCodeDir=E:/Projects/luban/_verify/langcode2 -x bin.outputDataDir=E:/Projects/luban/_verify/delta/client/language -x dataExporter=incremental-l10n-bin-split -x incremental.sidecarPath=E:/Projects/luban/_verify/_sidecar/language/_baseline.l10n.sidecar.json
```
Expected: `_verify/delta/client/language/{lang}/languageconfig.patch.bytes` 仅对变化语言产出；`_l10n.delta.manifest` 生成。检查 patch 前 4 字节 `LLP1`。

- [ ] **Step 4: 验证加语言列中止**

在 `language_l10n.xml` 给 Language bean 加一个语言字段，跑增量。
Expected: 报错 L10N 结构变化，不产 patch。还原。

- [ ] **Step 5: Commit**
```bash
git add src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs src/Luban.Core/Incremental/BaselineSidecarIO.cs
git commit -m "feat: incremental-l10n-bin-split 导出器（per-语言 key->value 行级 diff）"
```
（+ CHANGELOG。）

---

### Task 6: 导出脚本（slg2）

**Files:**
- Create: `D:\work\slg2\common\config\增量导出.bat`
- Create: `D:\work\slg2\common\config\增量导出.sh`
- Modify: `D:\work\slg2\common\config\1客户端（含本地化）.bat`/`.sh`（基准脚本：dataExporter 换 baseline-with-sidecar / l10n-baseline-with-sidecar，加 sidecar 路径）

**Interfaces:**
- Consumes: Task 1-5 的全部 exporter。
- Produces: 基准脚本（产 `Output/client` + `Output/server` + `Output/_sidecar`）；增量脚本（清 `Output/delta/` + 产 `Output/delta/client` + `Output/delta/server`）。

- [ ] **Step 1: 改基准脚本加 sidecar**

`1客户端（含本地化）.bat` 里 client 段 `-x dataExporter=tag-split` 改为：
```
-x dataExporter=baseline-with-sidecar ^
-x incremental.sidecarPath=.\Output\_sidecar\_baseline.client.sidecar.json ^
```
lang 段 `dataExporter=l10n-bin-split` 改为 `l10n-baseline-with-sidecar`，并加：
```
-x incremental.sidecarPath=.\Output\_sidecar\language\_baseline.l10n.sidecar.json ^
-x incremental.l10nChecksumPath=.\Output\client\language\_l10n.checksum.bytes ^
```
`.sh` 同步改。server 段不动（仍 `default` 全量）。

- [ ] **Step 2: 增量脚本**

Create `增量导出.bat`：
```bat
@echo off
setlocal
set LUBAN=Tools\Luban\Luban.dll
set CONF=Tools\game.conf
set LANG_CONF=Tools\lang.conf
set OUT=.\Output

echo [1/4] 清空增量目录
if exist %OUT%\delta rmdir /S /Q %OUT%\delta
mkdir %OUT%\delta\client %OUT%\delta\server 2>nul

echo [2/4] 客户端增量
dotnet %LUBAN% -t client -c cs-bin -d bin -i dev test -f --validationFailAsError --conf %CONF% ^
 -x cs-bin.outputCodeDir=/dev/null ^
 -x bin.outputDataDir=%OUT%\delta\client ^
 -x dataExporter=incremental ^
 -x incremental.sidecarPath=%OUT%\_sidecar\_baseline.client.sidecar.json
if errorlevel 1 goto fail

echo [3/4] 服务端全量（新）
dotnet %LUBAN% -t server -c java-json -d json -i dev test -f --validationFailAsError --conf %CONF% ^
 -x outputCodeDir=%OUT%\delta\server\code ^
 -x outputDataDir=%OUT%\delta\server\data ^
 -x java-json.codePackage=com.y.engine.server.data.config
if errorlevel 1 goto fail

echo [4/4] L10N 增量
dotnet %LUBAN% -t all -c cs-l10n-language -d bin -f --validationFailAsError --conf %LANG_CONF% ^
 -x cs-l10n-language.outputCodeDir=/dev/null ^
 -x bin.outputDataDir=%OUT%\delta\client\language ^
 -x dataExporter=incremental-l10n-bin-split ^
 -x incremental.sidecarPath=%OUT%\_sidecar\language\_baseline.l10n.sidecar.json
if errorlevel 1 goto fail

echo 增量导出完成：%OUT%\delta\
exit /b 0
:fail
echo 增量导出失败
exit /b 1
```
`.sh` 版镜像（用 `rm -rf`、`mkdir -p`、`$?` 检查）。

- [ ] **Step 3: 端到端验证**

```
cd D:\work\slg2\common\config
1客户端（含本地化）.bat        :: 基准，确认 Output\client + Output\server + Output\_sidecar 产出
:: 改一条 Building 数据 + 一条 LanguageText 文案
增量导出.bat                   :: 确认 Output\delta\client\*.patch.bytes + Output\delta\server\ + Output\delta\client\language\*.patch.bytes 产出
```
再跑一次 `增量导出.bat`（不改数据）：确认 `Output\delta\client\` 下无 patch（仅 manifest，changed=[]），证明全清生效（上次的 patch 被清掉，没残留）。

- [ ] **Step 4: Commit（slg2 仓库，若纳入 git）**
```bash
git -C D:/work/slg2 add common/config/增量导出.bat common/config/增量导出.sh common/config/1客户端（含本地化）.bat common/config/1客户端（含本地化）.sh
git -C D:/work/slg2 commit -m "feat: 增量导出脚本 + 基准脚本写 sidecar"
```
（slg2 若有 CHANGELOG 规范则同步；否则略。）

---

## Self-Review

**1. Spec coverage:**
- §4 SignatureId 算法 -> Task 1（StructureSignature，全字段不调 NeedExport，覆盖 bean/enum/容器/继承/children/index/mode）。✓
- §5.1 ChecksumInfo 加字段 -> Task 1 Step 3-4。✓
- §5.2 基准 sidecar -> Task 2（普通表）+ Task 4（L10N）。✓
- §5.4 目录隔离 + 全清 -> Task 6（脚本清 delta/）+ Global Constraints。✓
- §6.1 DLP1 格式 -> Task 3 PatchFormat + WritePatch。✓
- §6.2 LLP1 格式 -> Task 5。✓
- §6.3 _l10n.checksum.bytes -> Task 4。✓
- §6.4 manifest -> Task 3（_delta.manifest）+ Task 5（_l10n.delta.manifest）。✓
- §7 增量导出器流程 + 结构终止 -> Task 3（gate 全表扫、收集 offenders、throw 中止、不产文件）。✓
- §7 边界（新增表/删除表/无 sidecar/无变化）-> Task 3。✓
- §8 L10N 增量 -> Task 4+5。✓
- §3.2 完备管线（client 增量 + server 全量）-> Task 6 脚本。✓
- §9 运行时流程梳理 -> 不实现（第二期，本计划范围外，正确）。✓

**2. Placeholder scan:** 无 TBD/TODO。组合主键 `ExtractKey` 的特化在 Task 3 注释里点明（本工程组合索引需拼串），属实现提示非占位。`ctx.TargetName`/`ctx.Tables`/`GetTableExportDataList`/`IndexFieldIdIndex` 均有出处。L10N `WriteKey` 改 internal 在 Task 4 Step 2 明确。

**3. Type consistency:** `TableSidecarEntry.RowHashes: Dictionary<string,string>` 在 Task 2 写、Task 3 读，一致。`BaselineSidecar.Target`/`Tables` 一致。`DeltaManifest`/`DeltaManifestEntry` 在 Task 3 定义、Task 5 复用，一致。`PatchFormat.MagicTable`/`MagicL10N` 跨任务一致。`StructureSignature.ComputeForTable` 签名跨 Task 1/3/5 一致。

**4. 已知实现风险（执行时注意，非阻塞）：**
- `ExtractKey` 对组合索引（如 `type+level`）需拼成稳定串，否则 sidecar/diff key 对不上。Task 3 注释已点。执行时若 slg2 的组合索引表走增量，需特化。
- `L10NBinarySplitDataExporter` 的 `WriteKey`/`FindField` 等改 internal 需确认它们当前是 `private static`（Task 4 Step 2 假设）。执行时若已是 internal 跳过。
- `ctx.TargetName` 字段名需确认（若实为 `ctx.Target.Name` 等，执行时按实际改）。
- `DString.ValueOf` 签名在 ChecksumTableBuilder 已用，Task 1 Step 4 沿用，一致。
