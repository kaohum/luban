# 二进制导出字符串表化（bin String-Table）设计

日期：2026-08-25
状态：待评审
范围：luban 仓库（E:\Projects\luban）导出器 + 所有 bin 语言模板；slg 消费方（D:\work\slg）运行时 + 定制模板 + 手写读取端

## 1. 背景与目标

### 现状问题
- 当前 `bin` 格式：每条记录内联序列化，string 字段 = `WriteSize(字节数) + UTF-8 字节`（`BinaryDataVisitor.cs:73`）。相同字符串在文件里出现几次就写几次，字节层面重复。
- 运行时读取端：每个 string 字段调用 `ReadString()`，**每次 UTF-8 解码并分配一个新 string 对象**。表内重复字符串在内存中是各自独立的对象。
- 工具侧 `ByteBuf` 虽有 `StringCacheFinder` 缓存钩子，但 slg 客户端从未设置它——去重机制实际未启用。
- l10n 语言字典（`language/{lang}/languageconfig.bytes`）加载时全量解码所有文本（slg 场景文件可达 10MB 级）。

### 目标
1. **普通表**：全表字符串去重提取到文件头部，记录内 string 字段改为索引引用 → 重复字符串内存共享、解析提速（解码一次 vs 每处一次）。
2. **l10n 多语言文本**：懒解码——加载只解析索引不解码，`Get(id)` 首次取用才解码一次并缓存 → 加载零解码、未用文本零内存。
3. 服务器（slg 用 `java-json` + `json`）不受影响。

## 2. 决策记录（已与用户确认）

| 决策 | 结论 |
|---|---|
| A. 兼容性 | **直接替换默认 bin 格式**，不加任何格式标记/版本字节，旧客户端不兼容 |
| B. bin-offset | **保持原样不动**（slg 未使用，且该目标当前实现残缺——记录体未写入输出文件） |
| C. 普通表内存形态 | **v1 全量 eager**：加载即解码为 `string[]`，blob 用后即弃（与现流式模型一致） |
| D. l10n 内存形态 | **懒解码 + 缓存**：blob（约文件大小）常驻内存；本次不做缓存淘汰 |
| E. 语言范围 | **所有 bin 目标**：cs-bin / java-bin / go-bin / cpp-rawptr-bin / cpp-sharedptr-bin / typescript-bin / rust-bin / lua-bin |
| F. 增量 | **DLP1 / LLP2 patch 同步改造**，各自头部携带字符串表 |
| G. l10n 字典 | **一并处理**（语言字典 + LLP2） |

## 3. 现状梳理（调研结论）

### 3.1 二进制格式族（写端在 luban 仓库，读端在各消费方运行时）
| 文件 | 当前布局 | 写端 | 读端 |
|---|---|---|---|
| 全量表 `{table}.bytes` | `[WriteString(SignatureId)][WriteSize(count)][records…]` | `BinaryDataTarget.ExportTable` | 各语言生成表类 |
| DLP1 patch | `['D''L''P''1'][sig][upsertCount][upsert 记录][deleteCount][delete 主键]` | `IncrementalDataExporter.WritePatch` | 各语言生成 `MergeApply` |
| l10n 字典 `{lang}/languageconfig.bytes` | `[WriteSize(count)][WriteInt(id) WriteString(text)]*` | `L10NBinarySplitDataExporter.SerializeDictionaryToBinary` | **手写客户端代码**（`LanguageConfig.cs`） |
| LLP2 patch | `['L''L''P''2'][sig][upsertCount][WriteInt(id) WriteString(val)]*[deleteCount][WriteInt(id)]*` | `IncrementalL10NDataExporter.HandleSpace` | 手写 `LanguageConfig.ApplyDelta` |

### 3.2 消费方（slg）关键事实
- **客户端**（Unity 2022.3.62f3，C#）：`-c cs-bin -c cs-l10n-language -d bin`（omnibus 导出器）。运行时 `ByteBuf`/`StreamByteBuf` 在 `client/game/Packages/com.code-philosophy.luban@1.1.2/Runtime/`（**slg 仓库，不在 luban 仓库**）。
- **服务器**：`-c java-json -d json`——读 JSON，不受二进制格式影响。
- **流式加载**：`StreamByteBuf` 8KB 缓冲按需拉取，不整文件入内存；`ConfigTableInitializer` 复用单个实例，逐表 `Reset()`。当前内存模型 = 只留解析出的对象、不留原始字节。
- **代码生成模板两套**：slg 部署的 `Tools/Luban/Templates/` 中**只有 cs-bin 被定制**（`Create/Append/MergeApply` + `PooledHashMap/PooledList`，git 跟踪于 slg 仓库），其余语言模板与 luban 仓库一致。部署流程只拷贝 `Luban.dll`、不覆盖 Templates（`Luban.csproj:92`）。
  - 因此 slg 客户端代码生成 = **luban 仓库编译的 `Luban.CSharp.dll`（visitor 逻辑）+ slg 定制 cs-bin 模板**。改 `BinaryUnderlyingDeserializeVisitor`（string 发射）同时影响两套模板。
- l10n 读取端 `LanguageConfig.cs`（构造 + `ApplyDelta`）是**手写代码，非模板生成**。
- 生成表类有 `ExpectedSignatureId` 常量并在 `Create` 首行校验（`AccessConfig.cs:30`）；**该签名是 schema 结构签名**（`StructureSignature`），不含格式版本。
- 相关二次写入者（必须与主格式保持一致）：`BinaryIndexExportor`、`BaselineWithSidecarExporter`（行级 diff 用的记录字节）。

## 4. 新二进制格式

### 4.1 字符串表统一布局（索引区 + 连续 blob）

所有二进制产物共用一个字符串表布局：

```
[WriteSize(count)]                                   ← 去重后字符串数
[WriteSize(off0) WriteSize(len0)]                    ← 索引区：每串的 (blob 内偏移, 字节长)
[WriteSize(off1) WriteSize(len1)]
... 共 count 条
[UTF-8 字节流]                                       ← blob 区：连续拼接，offset 相对 blob 起点（0 基）
```

- **index = 首见顺序**（遍历 records 的确定性顺序），保证行字节确定、增量 diff 自洽。
- 空字符串自然入表去重，不特判；`null` 值映射到空串 index（与现状 round-trip 一致）。
- 该布局下读取端**只扫索引区即可拿到全部 (off,len)，无需解码 UTF-8、零 string 分配**——这是懒解码的基础。
- count=0 时无索引区、无 blob，读取端直接跳过。

### 4.2 全量表文件（bin）

```
[WriteString(SignatureId)]     ← 保持原样（schema 结构签名，不含格式版本）
[字符串表]
[WriteSize(recordCount)]
[records...]                   ← 所有 string 字段（含列表/映射/嵌套 bean/多态 bean 内）改为 WriteSize(stringIndex)
```

### 4.3 DLP1 增量 patch

```
['D''L''P''1']
[WriteString(SignatureId)]
[字符串表]
[WriteSize(upsertCount)]
[upsert 记录]*                 ← 记录体与全量一致（索引模式）
[WriteSize(deleteCount)]
[delete 主键索引]*             ← 主键若是 string，也入字符串表
```

### 4.4 l10n 语言字典 + LLP2

语言字典（`languageconfig.bytes` 等）：

```
[WriteSize(字符串表条目数)]
[索引区]
[blob]
[WriteSize(pairCount)]
[WriteInt(id) WriteSize(textIndex)]*
```

- l10n 字典**原本无签名头，本次也不新增**（代码与数据锁步升级、无需兼容校验；读取端按新布局解析）。

- 字符串表内容：所有 value 文本；**string-key space（indexMode=false，如 server space）的 key 字符串也入表**（读取端加载时 eager 解码 key，值懒解码）。
- int-key space（main/aot，indexMode=true）key 不进表。

LLP2 patch：

```
['L''L''P''2']
[WriteString(SignatureId)]
[字符串表]
[WriteSize(upsertCount)]
[WriteInt(id) WriteSize(valIndex)]*
[WriteSize(deleteCount)]
[WriteInt(id)]*
```

## 5. 运行时策略（C# 具体；其它语言见契约）

### 5.1 普通表：v1 eager
- `ByteBuf.ReadStringTable()`：解析索引区 + 顺序解码 blob → `string[]`；blob 用完即弃（不驻留，与现流式模型一致）。
- `ByteBuf.ReadStringIndex()`：`return _stringTable[ReadSize()];`。
- 字符串表状态挂在 **ByteBuf** 上 → 生成代码的 `Deserialize`/`ReadFrom` 签名不变，模板改动最小。
- `StreamByteBuf`：`ReadStringTable()` 基于 `virtual ReadSize/ReadString` 实现（或流式直读），自动继承其零分配解码优化。

### 5.2 l10n：懒解码 + 缓存
- 加载（`LanguageConfig` 构造）：解析索引区 → `(off,len)` 数组；**读 blob 拷贝进持久 `byte[]`**（10MB 级，常驻）；建 `id → stringIndex` 映射。
- `Get(id)`：`idx = map[id]` → `cache[idx]` 为空则 `Encoding.UTF8.GetString(blob, off, len)` 解码入缓存 → 返回。
- 缓存：`string[]`（初始全 null）；本次无淘汰。
- `ApplyDelta`（LLP2）：upsert 的 value 直接解码入缓存/映射；delete 移除映射。
- API 保持 `string`，消费方零改动。

### 5.3 各语言运行时 API 契约（消费方实现）
所有 bin 语言运行时 ByteBuf 需新增：
```
void   ReadStringTable();    // 解析头部字符串表（eager 语义），存内部状态
string ReadStringIndex();    // 返回表内引用；无表/越界 → 异常或空串
```
- 各语言生成表类构造函数/加载入口首行调用 `ReadStringTable()`；string 反序列化从 `readString()` 改为 `readStringIndex()`。
- C# 运行时由 slg 客户端包实现；Java/Go/Cpp/TS/Rust/Lua 的运行时在各消费方仓库，按本契约实现，本仓库只交付模板与文档。

## 6. 工具侧实现（luban 仓库）

| 位置 | 改动 |
|---|---|
| 新增 `StringTableWriter`（DataTarget.Builtin 或 Core/Serialization） | 两遍：遍历 records 收集去重字符串（Dictionary<string,int> 首见序）→ 写索引区 + blob |
| `BinaryDataVisitor` | 增加 string-index 模式：`Accept(DString)` 写 `WriteSize(index)`。实现为**可实例化 + 携带 StringTable 上下文**（保留 `Ins` 内联单例供 bin-offset 等使用），避免单例状态污染 |
| `BinaryDataTarget.ExportTable` | 先写字符串表，再索引模式写记录 |
| `IncrementalDataExporter.WritePatch` | DLP1 头部加字符串表 |
| `IncrementalL10NDataExporter.HandleSpace` | LLP2 头部加字符串表 |
| `L10NBinarySplitDataExporter.SerializeDictionaryToBinary` | 改 l10n 字典为索引区+blob 布局 |
| `BinaryIndexExportor`、`BaselineWithSidecarExporter` | 记录字节切到索引模式（与最终文件一致，保 diff 自洽） |
| `BinaryRecordOffsetDataTarget` | **不动**（决策 B） |

## 7. 代码生成模板改动

### 7.1 luban 仓库（所有 bin 目标）
每个语言（cs-bin/java-bin/go-bin/cpp-rawptr-bin/cpp-sharedptr-bin/typescript-bin/rust-bin/lua-bin）：
- 表类加载入口（table.sbn 等）首行：读字符串表。
- string 反序列化发射：`readString()` → `readStringIndex()`（cs/go/java/cpp/ts 在各语言 `*BinUnderlyingDeserializeVisitor`，rust/lua 在模板内联处）。
- DLP1 `MergeApply` 对应模板：magic+sig 后读 patch 字符串表，upsert/delete 用索引。
- Lua：`schema.sbn` 的 `InitTypes` 注册 `readStringTable`/`readStringIndex` 方法。
- cs-l10n-language 语言类模板：仅生成访问器，**二进制读取端是 slg 手写代码**（`LanguageConfig.cs`），模板本身无改动；实现时验证一次确认无生成读取端。

### 7.2 slg 定制 cs-bin 模板（D:\work\slg\common\config\Tools\Luban\Templates\cs-bin\）
- `table.sbn`：`Create`/`Append` 首行 `buf.ReadStringTable()`；`MergeApply` 在 magic+sig 后读 patch 字符串表。
- `tables.sbn` 如需同步（表集合加载入口）。
- string 字段发射无需改模板——由部署的 `Luban.CSharp.dll` 的 visitor 变更自动生效。

## 8. slg 消费方升级清单（D:\work\slg）

1. **运行时** `client/game/Packages/com.code-philosophy.luban@1.1.2/Runtime/ByteBuf.cs`：新增 `ReadStringTable()` / `ReadStringIndex()`。
2. **StreamByteBuf**：验证/适配两个新方法（基类实现应自动继承，需实测确认）。
3. **手写 l10n 读取端** `client/game/Assets/Scripts/Configs/LanguageConfig.cs`：懒解码实现（索引区 + blob 常驻 + `Get` 缓存），`ApplyDelta` 适配 LLP2。
4. **重新生成代码**（cs-bin）并重新导出全量 + 增量。
5. **重新基准**：行字节表示变化，重建 `Output/baseline/tables.json`、`l10n*.json`。
6. 其它语言运行时：按 5.3 契约由各自消费方升级（本仓库不交付运行时代码）。

## 9. 迁移与风险

- **直接替换、无兼容层**：旧客户端（未升级运行时）读新文件会脏读/异常——按决策 A 接受，要求全量同步升级。
- **SignatureId 不变**：schema 结构签名不含格式版本，跨版本换格式时旧校验仍可能"通过"后脏读；因无兼容需求，可接受。
- **重新基准**：行字节表示变化 → baseline 需重建；上线首个增量包前必须完成。
- **checksum 表** `checksumconfig.bytes` 走同一 `ExportTable` 路径自动获得新格式；客户端 `LanguageChecksumConfig` 随 cs-bin 重新生成适配。
- **非 C# 语言无法在本仓库验证**：模板与契约交付，运行时就绪性由各自消费方保证（标注为"已实现未验证"）。
- **bin-offset**：保持旧内联 visitor（决策 B），与主格式并存；其自身当前输出即残缺，后续如需使用单独修复。

## 10. 验收标准

1. cs-bin 全量导出 → C# 运行时读取，`DataList` 内容与导表前数据一致（round-trip）。
2. 表内重复字符串在运行时**共享同一对象**（`ReferenceEquals`）。
3. 普通表加载后 blob 不驻留（内存模型与现状一致）。
4. l10n：加载只扫索引（不产生 value 的 string 对象）；`Get(id)` 首次解码并缓存；未访问文本无 string 对象；blob 常驻。
5. DLP1 全量→增量热更链路跑通（`MergeApply`）；LLP2 链路跑通（`ApplyDelta`）。
6. 服务器 JSON 导出无任何变化。
7. 非 C# 模板产物按契约生成（代码可编译性由消费方验证）。
