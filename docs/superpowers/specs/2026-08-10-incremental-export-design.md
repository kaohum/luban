# 增量导表设计（基准 + 增量 + L10N）

- 日期：2026-08-10
- 状态：待 review
- 作者：陈昊然 / Claude
- 关联工程：Luban 源码 `E:\Projects\luban`；配置工程 `D:\work\slg2\common\config`；客户端 `D:\work\slg2\client\game`

> ⚠️ **2026-08-11 修订（以本文下方为准的部分已过时）**：checksumconfig 的版本值已从 **MD5** 改为**内容相关的时间戳 `Stamp`（long，unix 秒，可比大小）**；基准参照文件改名 `baseline/tables.json` / `baseline/l10n.json`（原 `_sidecar/...`）；sidecar 模型加 `ContentHash`+`Stamp`，manifest 加 `Stamp`。当前权威实现见 `CHANGELOG.md`（2026-08-11 段）与 `D:\work\slg2\docs\slg\config-increment-20260810\二期运行时实现交接.md`。本文的 SignatureId 算法、delta（DLP1/LLP1）、行键、结构 gate 等仍有效；凡涉及 `Checksum`(MD5) 字段、`_sidecar/` 路径、`_l10n.checksum.bytes` 的描述，按上述修订替换。

---

## 1. 背景与名词

- **基准配置 / 基准导表**：现有的全量导表模式。本次**行为不变**，仅在 checksum 上增加结构签名 `SignatureId`。
- **增量配置 / 增量导表**：新增模式。以基准为参照，diff 当前源数据 vs 已导出基准，**只产出 delta**。不跑数据校验、不算数据 checksum（行 hash 除外，那是 diff 内部量）。
- **运行时增量补丁**：增量产出的 patch 文件供客户端**运行时内存应用**，**不回写本地**。客户端重启走现有热更系统拉最新基准，增量不持久化。

### 1.1 现有基础设施（已存在，本设计复用）

| 已有 | 位置 | 说明 |
|---|---|---|
| per-table MD5 汇总表 `ChecksumConfig` | `Luban.Core/Checksum/ChecksumTableBuilder.cs` | 虚拟表 `{TableName, Checksum}`，全字段不过滤算 MD5（`BinaryChecksumVisitor`），前后端共享 |
| per-table `.bytes`（tag-split） | `Luban.DataTarget.Builtin/TagSplitDataExporter.cs` | 一表一文件，按 tag 分目录 |
| 客户端 `persistentDataPath` 优先读取 | `RawStreamProvider.cs` | 热更覆盖目录（本设计中**仅服务于热更基准**，不用于增量 patch） |
| 登录 proto 字段 | `PbUsers.proto` | `client_config_md5` / `config_check_map` / `client_configs: map<string,bytes>` |
| 客户端 `Tables.MergeApply` | `Tables.cs:2183` | 已生成但**不一致/无删除语义**，运行时阶段废弃重做 |
| L10N 拆语言导出 | `L10NBinarySplitDataExporter.cs` | `l10n-bin-split`，按语言拆 `key->string` |

### 1.2 现有基础设施的缺口

- 无结构指纹 / schema-id / 版本号（`DefBean.Id` 只是名字 hash，加字段不变，不能当结构版本）。
- bin 格式无文件头、无行 framing（`{count}{records}`），旧文件无法直接 patch。
- `MergeApply` 各表行为不一致（Building 截断重建=全量替换；Access 只 upsert 不删；ONE 表无 MergeApply）。
- L10N 无任何 checksum / 版本 / patch 机制；`LanguageConfig` 是 `readonly dataMap` + 仅 `new`，无合并方法。
- 服务端 `ClientConfigStore.getDiffConfigs` 未实现。

---

## 2. 目标与范围

### 2.1 本期交付（本 spec 范围）

1. **Luban 导出工具改造**：
   - 基准导出：`ChecksumInfo` 增加 `SignatureId` 字段；写基准 sidecar。
   - 增量导出器 `[DataExporter("incremental")]`（普通表）。
   - L10N 增量导出器 `[DataExporter("incremental-l10n-bin-split")]`。
   - L10N checksum 产物 `_l10n.checksum.bytes`。
2. **全部格式契约**：delta（DLP1 / LLP1）、`checksumconfig`（+SignatureId）、`_l10n.checksum`、sidecar、manifest。
3. **运行时流程梳理**（描述级，不改代码）。

### 2.2 不在本期范围

- ❌ C# / Java 运行时代码实现（第二期）。
- ❌ proto 修改（第二期，服务器侧）。
- ❌ 服务端 `ClientConfigStore` 实现（第二期）。

### 2.3 关键决策记录

| 决策 | 结论 | 理由 |
|---|---|---|
| 增量粒度 | 普通表行级（按主键 upsert+delete）；L10N 按 (语言, 文本 key) | 用户三例均行级；"结构变化终止"只在行级才有意义 |
| delta 基准 | 相对基准重算（基准->当前累计），服务器只留最新一份 | 简单、幂等、服务器免解析 payload |
| 服务器角色 | 只透传 patch 字节，仅解析元数据（SignatureId/Checksum） | 复用现有 `client_configs: map<string,bytes>`；服务器有自己独立的 java-json 配置管线，不进本体系 |
| SignatureId 命名 | `SignatureId`（带 Id 后缀，但靠 Signature/Type 与 `GetTypeId` 区分） | 用户选定；避开业务 Schema 命名 |
| `GetTypeId` | **保留不动** | 它是多态判别符（`DefBean.Id`=名字 hash），与结构签名无关；删除会破坏多态 bean 序列化（12 语言模板+核心），且与本期无关 |
| SignatureId 字段范围 | **全字段、不过 group 过滤**，前后端共享 | 沿用 `BinaryChecksumVisitor` 既定原则；c/s 导出不同字段子集但同值。代价：加仅服务端字段也 bump 它（保守，符合"结构变即新基准"） |
| baseline diff 参照 | sidecar（工具内部，per-target，JSON） | 免反序列化旧 .bytes；结构 gate 天然在此做 |
| 增量 patch 持久化 | **不回写本地**，in-memory apply | 用户澄清：重启走热更系统；patch 幂等可登录重放 |
| L10N checksum | 平行独立产物 `_l10n.checksum.bytes`，不并入 `checksumconfig` | L10N 是独立管线（lang.conf），跨管线拼数据很脏 |
| proto | 不改 | 第二期服务器侧 |

---

## 3. 总体架构

**不新增 Pipeline**，复用 `DefaultPipeline`。新增三块代码，都落在既有扩展点：

1. **SignatureId 计算**：挂进现有 `GenerationContext.CalculateTableChecksums`（`GenerationContext.cs:168`），给每张表多算一个结构签名。
2. **sidecar 写入**（基准）：基准导出末尾多写工具内部文件。
3. **增量导出器**：`[DataExporter("incremental")]`（普通表）/ `[DataExporter("incremental-l10n-bin-split")]`（L10N）。

两种模式通过 `-x dataExporter=...` 选择，互不干扰。

### 3.1 一次发布周期

```
[出新版本 / 结构大改]
  基准导出 -> configs/basic/*.bytes + checksumconfig.bytes(+SignatureId) + _baseline.client.sidecar.json
             language/{lang}/languageconfig.bytes + _l10n.checksum.bytes + _baseline.l10n.sidecar.json
             （.bytes + checksum 上传后台 -> 服务器持有；sidecar 留构建环境）

[期间只改数据，可多次]
  增量导出 -> 读 sidecar -> diff -> Output/delta/（client 增量 + server 全量新，先全清再写）
             （patch 上传后台 -> 服务器替换最新增量；客户端结构签名一致则只取增量，in-memory apply）

[再出新版本 / 结构大改]
  基准导出 -> 刷新 sidecar，delta 清零，新一轮开始
```

### 3.2 完备导出管线与各 target 模式

增量导出是一套**完备的导出管线**：一个导出脚本编排所有 target，每个 target 按需选模式。增量导出器本身是 **target 无关**的（按当前 target 的 group 过滤算 rowHash），天然支持不同语言。

| target | 模式 | 产出 |
|---|---|---|
| client（cs-bin） | **增量** | 基准 .bytes + checksumconfig(+SignatureId) + sidecar + delta patch |
| server（java-json） | **全量** | 全量 java-json（**不变**，服务器不需要增量） |
| L10N（cs-l10n-language） | **增量** | 基准 languageconfig + _l10n.checksum + sidecar + delta patch |

- **服务器不需要增量**：服务器自有配置走全量重载。导出脚本里 server target 用默认全量导出（`dataExporter=default`），**不产 sidecar、不产 delta**。
- 服务器仍会生成 checksumconfig（含 SignatureId，因 SignatureId 计算在共享的 `CalculateTableChecksums` 里），但服务器自身不消费它做增量；它与客户端同值（前后端一致），仅作一致性参考。
- 服务器**比对客户端 SignatureId 时用的值**来自客户端基准导出后上传的 `checksumconfig`（服务器持有），服务器不自己算客户端的 SignatureId。

---

## 4. SignatureId 算法

### 4.1 定义

**= 大写 hex MD5(规范化结构描述串)**，输出格式与现有 `FileUtil.CalcMD5` 一致。

### 4.2 覆盖维度（全字段、不过 group 过滤）

| 维度 | 进不进 |
|---|---|
| 表全名、mode（ONE/MAP/LIST）、index 描述（主键字段+类型、次级索引） | ✅ |
| 行 bean 全名、继承父类（递归） | ✅ |
| 全部字段（不过滤 group）：字段名、类型、可空 | ✅ |
| bean 类型字段 -> 递归该 bean 结构；enum 类型字段 -> 该枚举全量成员(name+value) | ✅ |
| 容器类型 -> 元素/键值类型递归 | ✅ |
| 数据值、注释 | ❌ |

**触发变化**：加/删/改字段、改类型/可空、改枚举项、改继承、改 index/mode、改名、加/删语言列（L10N）。
**不触发**：只改数据值。

### 4.3 与语种/目标无关（核心契约）

遍历 `DefBean.HierarchyFields`（全部已声明字段，含继承），**不调用 `NeedExport(field)`**。c-target 导出 10 字段、s-target 导出 8 字段，但两边 `HierarchyFields` 是同一份全量字段集 -> SignatureId 字节级相同。

**沿用 `BinaryChecksumVisitor.cs:123` 的既定原则**（注释明说"不过滤字段，保证前后端不同 group 过滤下 MD5 一致"）。

### 4.4 实现

把现成的 `DeepCompareTypeDefine`（`TypeVisitors/DeepCompareTypeDefine.cs:27`，已会深度遍历比结构）改造成 hash 累加器：per-bean 递归算签名并 memoize。

- 普通表：`SignatureId = MD5(表名 + mode + index描述 + 行bean签名)`
- L10N：`SignatureId = MD5(Language bean 全结构)`，全语言共享一个值。

### 4.5 与 `GetTypeId` 的区别（不冲突、不复用）

|  | `GetTypeId` | `SignatureId`（新增） |
|---|---|---|
| 语义 | 名字身份（多态分发） | 结构版本（增量可用性） |
| 输入 | `FullName`（`ComputeCfgHashIdByName`，31*acc+char） | 全名 + 字段布局 + index/mode |
| 粒度 | per-bean | per-table（普通表）/ per-Language-bean（L10N） |
| 产生时机 | scriban 生成进代码，运行时调用 | 导出时 C# 算一次，写进 checksum 当数据 |
| 消费者 | bin 反序列化 | checksum / sidecar / 服务器比对 |
| 改字段会变吗 | 不会 | 会 |

`GetTypeId` / `DefBean.Id` / `ITypeId` / `BeanBase` / `BinaryDataVisitor` 写 id 路径 / 全部语言模板：**完全不动**。

---

## 5. 基准导出改造

### 5.1 `ChecksumInfo` 加字段

`ChecksumTableBuilder.cs:32`：`ChecksumInfo { TableName, Checksum }` -> `{ TableName, Checksum, SignatureId }`。

`CalculateTableChecksums`（`GenerationContext.cs:168`）里，每张表同时算：
- `Checksum`（数据 MD5，**逻辑不变**，全字段、前后端共享）
- `SignatureId`（结构，**新**）

`ChecksumConfig` 每表变成 `{ TableName, Checksum, SignatureId }`。

> 一次性 baseline bump：`ChecksumInfo` 结构变了 -> C#/Java 生成代码重生成，预期内。

### 5.2 基准 sidecar（工具内部，不 ship）

普通表：`_baseline.{target}.sidecar.json`，per table：
```json
{
  "target": "client",
  "tables": {
    "BuildingConfig": {
      "signatureId": "A1B2...",
      "mode": "map",
      "primaryKeyIndex": "id",
      "rowCount": 1234,
      "rowHashes": { "<key>": "<MD5>", ... }     // 按 target 可见字段(group 过滤)算
    }
  }
}
```

L10N：`language/_baseline.l10n.sidecar.json`：
```json
{
  "signatureId": "C3D4...",                      // Language bean SignatureId，全语言共享
  "languages": {
    "zh_CN": { "rowHashes": { "<key>": "<MD5(value)>" } },
    "en_US": { ... }
  }
}
```

**rowHash 按"目标可见字段"算**（group 过滤后）--只改服务端字段不触发客户端 delta。`SignatureId` 则相反（全字段）。

sidecar 是 **per-target** 的（文件名带 target），因为 rowHashes 按 target 可见字段算。

**sidecar 保留与 CI 传递**：sidecar 是工具内部产物，但**基准导出**和后续**增量导出**可能在不同 CI 作业/不同机器跑。因此 sidecar 必须作为构建产物在两次运行间持久化（check-in 到配置仓库，或作为 CI artifact 传递）。基准导出刷新 sidecar；增量导出只读不写。sidecar 丢失 = 增量无法跑（报"先跑基准导出"）。

### 5.3 产出物总览

| 产物 | 来源 | ship? | 谁解析 |
|---|---|---|---|
| `client/configs/basic/{table}.bytes` | 基准 | ✅ | 客户端全量加载（不变） |
| `client/configs/basic/checksumconfig.bytes` | 基准 | ✅ | 客户端读 `{TableName, Checksum, SignatureId}` 上报；服务器比对 |
| `delta/client/basic/{table}.patch.bytes` (DLP1) | 增量 | ✅ | 客户端 `MergeApply`；服务器透传 |
| `delta/client/basic/_delta.manifest` | 增量 | ✅ | 服务器决定发哪些表；客户端校验 SignatureId |
| `client/language/{lang}/languageconfig.bytes` | 基准 | ✅ | 客户端全量加载（不变） |
| `client/language/_l10n.checksum.bytes` | 基准 | ✅ | 客户端读 signatureId + per-lang MD5 上报；服务器比对 |
| `delta/client/language/{lang}/languageconfig.patch.bytes` (LLP1) | 增量 | ✅ | 客户端 `ApplyDelta`；服务器透传 |
| `delta/client/language/_l10n.delta.manifest` | 增量 | ✅ | 服务器决定发哪些语言 |
| `server/...` (java-json) | 基准 | ✅ | 服务端自有配置全量加载（不变） |
| `delta/server/...` (java-json) | 增量 | ✅ | 服务端全量新产出（服务器不做增量） |
| `_sidecar/_baseline.*.sidecar.json` | 基准 | ❌ | 仅导出器 diff 用 |

### 5.4 目录规划与上传流程

```
Output/
├── client/                  # 基准导出（客户端全量）
│   ├── configs/basic/{table}.bytes + checksumconfig.bytes(+SignatureId)
│   └── language/{lang}/languageconfig.bytes + _l10n.checksum.bytes
├── server/                  # 基准导出（服务端全量）
│   └── ... (java-json)
├── delta/                   # 增量导出（每次开工前整个清空，绝不 append）
│   ├── client/              # 客户端增量
│   │   ├── basic/{table}.patch.bytes + _delta.manifest
│   │   └── language/{lang}/languageconfig.patch.bytes + _l10n.delta.manifest
│   └── server/              # 服务端全量（新产出，服务器不做增量）
│       └── ... (java-json)
└── _sidecar/                # 工具内部（基准写/增量读），不在任何上传目录下
    ├── _baseline.client.sidecar.json
    └── language/_baseline.l10n.sidecar.json
```

**路径布局**：`Output/` 下 `client/`、`server/`、`delta/` 三者同级独立，分别表示客户端基准、服务端基准、增量。`delta/` 下再分 `client/`（客户端增量）和 `server/`（服务端全量新产出，因服务器不做增量）。`_sidecar/` 独立于所有上传目录，不被误上传。

**两条上传流程**：
- **出新基准**（结构变 / 大版本）：上传 `Output/client/` + `Output/server/` -> 服务器持有为新基准。
- **出增量**（期间改数据）：上传 `Output/delta/`（整个目录，含 client 增量 + server 全量新）-> 服务器替换最新增量。server 若没变可只传 `delta/client/`。

**增量目录每次全清（绝不 append）**：
- 增量导出**开工前先把 `Output/delta/` 整个清空**（删除 delta/ 下所有旧内容，含 client/ 和 server/），再写本次产出。
- 原因：delta 永远是"基准->当前"重算，上一次的 patch 已过期，绝不能残留与新 patch 混在一起。
- 实现：脚本级先清空 `Output/delta/`，再跑 client 增量（写 `delta/client/`）+ L10N 增量（写 `delta/client/language/`）+ server 全量（写 `delta/server/`）。

**目录设计要点**：
- `delta/client/` 内部结构镜像 `client/`（`basic/`、`language/{lang}/`），patch 路径能对应到表。
- patch 按 tag 分目录（镜像基准 tag-split）：`delta/client/basic/`、`delta/client/test/` 等。
- `_sidecar/` 由基准导出写、增量导出读，必须 CI 间持久化（见 5.2）；不在任何上传目录下。

---

## 6. delta 文件格式

### 6.1 普通表 patch（`{table}.patch.bytes`，magic `DLP1`）

```
magic        : 4 字节 ASCII "DLP1"            // Delta Patch v1
signatureId  : WriteString                    // 本 patch 依据的基准 SignatureId；客户端应用前校验
upsertCount  : WriteSize
upsert rows  : 每条 = ExportRecord 序列化的整行字节
               （与该行在全表 .bytes 里的字节布局完全一致，按目标 group 过滤）
deleteCount  : WriteSize
delete keys  : 每条 = 主键字段按其类型序列化（仅主索引字段；联合主键多写几个）
```

- upsert 行字节 = `ExportRecord` 产出，客户端复用现有 `DeserializeXxx(buf)`/`ReadFrom(buf)`，零新反序列化代码。
- delete 只编码主键；运行时按主键从主 map 移除 + 重建次级索引。
- signatureId 嵌进每个 patch（自描述）：客户端应用前比自己基准 SignatureId，不一致拒。
- 三种情形：只增/改 -> deleteCount=0；只删 -> upsertCount=0；无变化 -> 不产 patch 文件。
- **ONE 表**：upsertCount ≤1、deleteCount 恒 0（单记录，只有"替换/不变"）。
- **LIST 表**（工程里 0 张，防御性）：delta 里退化成"整表替换"。
- magic 仅自描述；dispatch（patch vs 全表）由运行时/协议层决定。

### 6.2 L10N patch（`languageconfig.patch.bytes`，magic `LLP1`）

```
magic        : 4 字节 ASCII "LLP1"            // L10N Language Patch v1
signatureId  : WriteString                    // Language bean SignatureId（全语言共享）
upsertCount  : WriteSize
  重复: [WriteString key][WriteString value]  // 与 languageconfig.bytes 里 (key,value) 编码完全一致
deleteCount  : WriteSize
  重复: [WriteString key]
```

upsert 的 `(key,value)` 字节布局与全量 `languageconfig.bytes` 逐条一致 -> 客户端复用 `ReadString()`。

### 6.3 `_l10n.checksum.bytes`（L10N checksum，平行于 checksumconfig）

```
[WriteString: signatureId]                    // Language bean SignatureId，全语言共享
[WriteSize: langCount]
  per lang: [WriteString: langName][WriteString: dataMD5]
```

### 6.4 delta manifest（服务器侧 patch 索引，JSON）

`_delta.manifest` / `_l10n.delta.manifest` 是**服务器解析**的元数据（不入客户端），告知服务器"有哪些 patch 文件可用 + 各自依据的基准 SignatureId + upsert/delete 计数"。格式 JSON（服务器侧，第二期可调）：

```json
{
  "baselineSignatureId": "A1B2...",       // 或 L10N 的共享 signatureId
  "sidecarVersion": "2026-08-10-001",
  "changedTables": [
    { "table": "BuildingConfig", "upsertCount": 3, "deleteCount": 1, "patchFile": "BuildingConfig.patch.bytes" }
  ]
}
```

服务器据此 + 客户端上报的 checksum 判定发哪些 patch。

---

## 7. 增量导出器流程（普通表）

**触发**：`-x dataExporter=incremental` + `-x incremental.sidecarPath=.../_sidecar/_baseline.client.sidecar.json` + `-x bin.outputDataDir=Output/delta/client`。**开工前脚本先清空 `Output/delta/`**（整个增量目录，含 client/ 和 server/），再跑 client 增量（写 `delta/client/`）+ L10N 增量（写 `delta/client/language/`）+ server 全量（写 `delta/server/`，`dataExporter=default`）。

`[DataExporter("incremental")]` 的 `Handle`：

1. **读 sidecar**（per-target）。找不到 -> 报错终止。校验 sidecar target 标记 == 当前 target；不符 -> 报错。
2. **第一遍 · 结构 gate（全表扫）**：逐表算当前 SignatureId，与 sidecar 比对，收集所有「SignatureId 不一致 / 新增表 / 消失表」。
3. **若有任何结构不一致 -> 整批终止**，一次性报全部清单，**不产出任何 patch / manifest**。
4. **第二遍 · 行 diff（仅结构全一致时）**，逐表：
   - 算当前 rowHashes（主键->MD5，按目标 group 过滤字段，用 `ExportRecord`）。
   - 与 sidecar rowHashes diff：新主键 -> upsert；hash 变 -> upsert；sidecar 有/现无 -> delete；相同 -> 跳过。
   - 无变化 -> 不产 patch；有变化 -> 产 `{table}.patch.bytes` + 记进 manifest。
5. 写 `_delta.manifest`（基准 SignatureId 快照、变化表清单 + upsert/delete 计数、依据的 sidecar 版本）。
6. 全程**不算数据 checksum、不跑 validator**。

### 7.1 结构终止错误信息

```
[增量导出已终止] 检测到结构变化，无法在旧基准上叠加增量。请重新执行基准导出（会刷新 sidecar）。
  - Building   : SignatureId 期望 aB3F.. 实际 9c2E.. （结构变化）
  - NewTableX  : 基准中不存在 （新增表，需新客户端代码）
  - OldTableY  : 当前已移除 （删除表）
本次未产出任何 delta 文件。
```

### 7.2 边界情况

| 情况 | 处理 |
|---|---|
| 找不到 sidecar | 报错：先跑基准导出 |
| sidecar target ≠ 当前 target | 报错：target 不匹配 |
| 新增表（sidecar 无） | 结构变化 -> 终止（新表要新客户端代码 = 基准事件） |
| 删除表（当前无） | 结构变化 -> 终止 |
| SignatureId 不一致 | 结构变化 -> 终止（主 gate） |
| 全表均无变化 | 写空 manifest（changed=[]），不产 patch，日志"无变化" |
| L10N 多语言表 | 不在本导出器（走 incremental-l10n-bin-split） |

**原子性**：所有 patch + manifest 在内存构好再落盘（沿用 `LocalFileSaver`）；中途异常不产出半截 delta。

---

## 8. L10N 增量

### 8.1 接入点

`[DataExporter("incremental-l10n-bin-split")]`，复用 `L10NBinarySplitDataExporter` 的"找语言字段 / 拆 key / 序列化"逻辑（抽成 internal 共享），外层套"读 sidecar -> diff -> 出 delta + 刷新 sidecar"。lang.conf 里把 `dataExporter=l10n-bin-split` 换掉即可。

### 8.2 L10N 数据模型（已查证）

- 文本源：`LanguageCode.csv` + `LanguageText.csv`，`LanguageText` 是 **key × 14 语言** 矩阵。
- `l10n-bin-split` 按语言拆列 -> 每语言一份 `language/{lang}/languageconfig.bytes`，内容 `key -> 该语言文本`。
- 全量 .bytes 布局：`[varint count] [key, value]*`（无 magic/版本/header）。
- **所有语言共用同一份 key 集合**：导出器对每个 key 在每种语言都写一条，缺失语言写空串。

### 8.3 diff 语义（按文本 key，per 语言）

- diff 单位 = (语言, 文本 key)，与普通表按主键平行。
- 某 key 的文案在 `en_US` 变 -> 只在 `en_US` 产 1 条 upsert；其它语言不受影响。
- 新增 key -> 在所有语言各产 1 条 upsert（值可能空串）。
- 删除 key -> 在所有语言各产 1 条 delete。
- **空串 = 合法 value，不特殊处理**（"空串 vs key 不存在"的坑因 key 共享而消解）。

### 8.4 SignatureId（L10N）

= `MD5(Language bean 全结构)`：`key` 字段(名+类型) + 全部 14 语言列(名+类型) + 序列化编码。全字段、不过滤、前后端共享。

- 加/删/改语言列、改 key 类型 -> SignatureId 变 -> 全语言增量整批终止 -> 新基准。
- 只改文本值 -> 不变 -> 行级 diff。
- "新增一种语言"被结构 gate 捕获（新语言列=结构变化=新基准）。新语言本就要新 `LanguageType` 枚举=新客户端代码=基准事件，一致。

### 8.5 流程

镜像普通表：读 `_baseline.l10n.sidecar.json` -> 第一遍结构 gate（SignatureId）-> 第二遍 per 语言 `key->MD5(value)` diff -> 产 `languageconfig.patch.bytes`（LLP1）+ `_l10n.delta.manifest`。

### 8.6 范围边界

| conf | 进增量？ | 原因 |
|---|---|---|
| `lang.conf`（客户端运行时语言） | ✅ | 客户端运行时数据，热更目标 |
| `langServer.conf`（服务端自己的语言） | ❌ | 服务端自有配置，全量重载 |
| `langAOT.conf` | ❓ **待确认** | 疑似 codegen（`language.sbn` 生成静态 `_{key}` 字段，3000 key 硬上限）。若纯 codegen=基准事件不进；若也产运行时 `.bytes` 则同 lang.conf |

---

## 9. 前后端解析契约 + 运行时流程梳理（不改代码、不改 proto）

### 9.1 服务器角色

- 服务器有自己独立的 java-json 配置管线（`gen_server.sh`），**不进本体系**。
- 客户端的基准/增量通过后台（运营 portal）上传，服务器**透传**给客户端。
- 服务器**只解析元数据**（SignatureId / Checksum / MD5 / manifest）判一致性，**不解析 patch payload**。

### 9.2 运行时流程梳理

```
[启动]  热更系统交付当前基准 -> 加载 configs/basic/*.bytes + language/{lang}/*.bytes（全量）
        + 加载 checksumconfig（含 SignatureId）+ _l10n.checksum.bytes

[登录]  客户端上报 { per-table{SignatureId,Checksum}, L10N{signatureId, per-lang MD5} }
        服务器逐表/逐语言判定:
          SignatureId 不一致或无基准 -> 下发全量 .bytes
          SignatureId 一致但数据落后 -> 下发 patch.bytes
          已最新                    -> 不下发
        （proto 字段第二期定；本期只梳理判定逻辑）

[应用]  全内存，不落盘:
          全量  -> 替换内存 Table / LanguageConfig 对象
          patch -> MergeApply / ApplyDelta 叠加到内存对象
        （不写 persistentDataPath；RawStreamProvider 的 persistentDataPath 优先级只服务于热更基准）

[运行中] 服务器可推送新增量 -> in-memory apply

[重启]   回到[启动]：热更拉最新基准，登录再按需补增量（patch 幂等可重放）
```

### 9.3 第二期工作量清单（非本期，仅记录契约）

**客户端**：
- 重生成 per-table `MergeApply`（吃 DLP1，upsert+delete+SignatureId 校验）；现有不一致/无删除，废弃重做。
- ONE 表补 replace 路径（GameSettings 等无 MergeApply）。
- `LanguageConfig.ApplyDelta(ByteBuf)`：`dataMap` 改可变 + 改 `language.sbn` 模板生成。
- `_l10n.checksum.bytes` 小 loader（~15 行 ByteBuf 读取）。
- `ResourceCheckHelper` 接线：登录上报、收到 patch 调合并器（in-memory，不落盘）。

**服务端**：
- `ClientConfigStore.getDiffConfigs(...)`：持有基准 + 最新 patch，按上报 SignatureId/Checksum 判定全量/增量，塞进 `client_configs`。
- L10N 按语言维度持有 + 下发。

**proto**：第二期服务器定（现有 `client_config_md5`/`config_check_map`/`client_configs` 可能够用或扩展）。

---

## 10. 关键文件索引（实现时落点）

### 10.1 Luban 源码（`E:\Projects\luban\src`）

| 文件 | 改动 |
|---|---|
| `Luban.Core/Checksum/ChecksumTableBuilder.cs:32` | `ChecksumInfo` 加 `SignatureId` 字段 |
| `Luban.Core/GenerationContext.cs:168` | `CalculateTableChecksums` 同时算 SignatureId；写基准 sidecar |
| `Luban.Core/TypeVisitors/DeepCompareTypeDefine.cs:27` | 改造成 hash 累加器（StructureSignatureVisitor） |
| `Luban.DataTarget.Builtin/`（新文件） | `[DataExporter("incremental")]` 增量导出器 |
| `Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs` | 抽共享逻辑为 internal；新 `[DataExporter("incremental-l10n-bin-split")]` |
| `Luban.Core/Utils/FileUtil.cs:104` | 复用 `CalcMD5` |
| `Luban.Core/DataTarget/IDataTarget.cs:42` | 复用 `ExportRecord` 产单行字节 |
| `Luban.Core/Defs/TableDataInfo.cs:28` | 复用 `IndexKey`（主键定位，已实现 Equals/GetHashCode） |

### 10.2 slg2 配置工程（`D:\work\slg2\common\config`）

完备导出脚本（一个脚本编排所有 target，按模式产出）：

| 脚本步骤 | target | dataExporter | 产出 |
|---|---|---|---|
| 基准模式 | client | `tag-split`（不变） | `client/configs/*.bytes` + checksumconfig(+SignatureId) + `_sidecar/` |
| 基准模式 | server | `default`（不变） | `server/`（java-json 全量） |
| 基准模式 | L10N | `l10n-bin-split`（不变） | `client/language/` + _l10n.checksum + `_sidecar/` |
| 增量模式 | client | `incremental` | `delta/client/*.patch.bytes` + _delta.manifest |
| 增量模式 | server | `default`（全量，不变） | `delta/server/`（java-json 全量新） |
| 增量模式 | L10N | `incremental-l10n-bin-split` | `delta/client/language/*.patch.bytes` + _l10n.delta.manifest |

- **基准脚本** = 现有 `1客户端（含本地化）.bat` + `2服务器.bat` + lang 三段，**行为不变**（自动多产 SignatureId + sidecar）。client 写 `Output/client/`，server 写 `Output/server/`。
- **新增增量脚本**：**开工前清空 `Output/delta/`**；client/L10N 走增量导出器（写 `delta/client/`），server 走全量（写 `delta/server/`）。`-x incremental.sidecarPath=.../_sidecar/_baseline.client.sidecar.json`。

### 10.3 客户端（`D:\work\slg2\client\game`，**第二期**）

| 文件 | 改动 |
|---|---|
| `Assets/Scripts/Configs/Gen/Tables.cs:2183` | 重生成 `MergeApply` 吃 DLP1 |
| `Assets/Scripts/Configs/Gen/{Table}Config.cs` | 各表 MergeApply 重做；ONE 表加 replace |
| `Assets/Scripts/Configs/LanguageConfig.cs` | `dataMap` 改可变 + `ApplyDelta` |
| `Luban.CSharp/Templates/cs-l10n-language/language.sbn` | 生成 `ApplyDelta` |
| `Assets/Scripts/Game/Runtime/Modules/Login/ResourceCheckHelper.cs` | 接线：上报 + in-memory apply |

---

## 11. 验收标准

### 11.1 基准导出

- [ ] `checksumconfig.bytes` 每表含 `{TableName, Checksum, SignatureId}`。
- [ ] 同一 schema 跑 c-target 和 s-target，每表 SignatureId 完全相同。
- [ ] 加字段/删字段/改类型/改可空/改枚举项/改 index -> SignatureId 变；只改数据值 -> 不变。
- [ ] 产出 `_baseline.{target}.sidecar.json`（普通表）+ `language/_baseline.l10n.sidecar.json`（L10N）。
- [ ] 产出 `language/_l10n.checksum.bytes`。

### 11.2 增量导出（普通表）

- [ ] 10 行 -> 11 行：产 1 条 upsert。
- [ ] 10 行 -> 11 行且 1 行变化：产 1 upsert + 1 upsert（变化）。
- [ ] 10 行 -> 9 行：产 1 delete。
- [ ] 无变化：不产 patch，空 manifest。
- [ ] 结构变化（任一表 SignatureId 不一致 / 新增表 / 删除表）：整批终止，错误清单列出全部，不产任何文件。
- [ ] patch 文件含 magic `DLP1` + signatureId + upsert 段 + delete 段。
- [ ] upsert 行字节与全表 .bytes 中对应行字节一致。

### 11.3 增量导出（L10N）

- [ ] 某语言某 key 文案变：只在该语言产 1 upsert。
- [ ] 新增 key：所有语言各产 1 upsert。
- [ ] 删除 key：所有语言各产 1 delete。
- [ ] 加语言列：SignatureId 变 -> 整批终止。
- [ ] patch 文件含 magic `LLP1` + signatureId + upsert(key,value) + delete(key)。

### 11.4 契约

- [ ] patch payload 服务器零解析（仅透传）。
- [ ] 客户端 patch 应用 in-memory，不写 persistentDataPath。

---

## 12. 待确认 / 开放项

1. **langAOT.conf 是否进增量**：疑似 codegen（静态字段），若纯 codegen 则不进。需确认。
2. **L10N patch 是否需要 mid-session 推送**：当前梳理仅登录时下发；运行中推送是否需要，第二期定。
3. **proto 字段是否够用**：`client_config_md5`/`config_check_map`/`client_configs` 能否承载 SignatureId + per-language 维度，第二期服务器确认。
