# 多语言 int 下标改造 + 导表管线合并设计(Tier 2)

- 日期:2026-08-21
- 状态:待 review
- 作者:陈昊然 / Claude
- 关联工程:Luban 源码 `E:\Projects\luban`;配置工程 `D:\work\slg\common\config`;客户端 `D:\work\slg\client\game`
- 前置文档:`docs/superpowers/specs/2026-08-10-incremental-export-design.md`(增量导表体系,本设计依赖其 sidecar/DLP1/LLP1/checksum 基础)

---

## 1. 背景与目标

现状:语言文本以 string key 贯穿全链路——

- 语言 bin `{lang}/languageconfig.bytes` 为 `{count}{(string key, string value)*}`,key 重复存储于每种语言文件;
- 业务表引用语言 key 的字段(如 `Item.nameId`)是普通 `string`,每行重复存储 key 字符串;
- 客户端运行态 `Dictionary<string,string>`,内存高、解析慢;
- 增量 patch LLP1 按 string key diff/upsert。

目标:**策划填写层保留 string key(锚点不变),实现层彻底取缔 string key**:

1. 语言表按**固定下标**导出为纯字符串数组,业务上的 string key 恒定对应一个数组下标,`LanguageConfig.{key} => dataArray[n]`;
2. 业务表引用字段改用现有 **`text` 类型**(string 语法糖),导表期解析为 int 下标;
3. 下标分配**跨次导表幂等**(上次导表与下次导表下标不变),兼容增量模式(只允许改旧值 + 尾部追加,禁止中间插入);
4. 顺便合并导表管线:3 条语言管线(lang / langServer / langAOT)并入 game.conf,导出收敛为 2 次 Luban 调用(客户端 omnibus + 服务器),消除跨管线 key 映射传递与 bat 顺序约束。

### 1.1 关键决策记录

| 决策 | 结论 | 理由 |
|---|---|---|
| 字段类型载体 | **复用现有 `text` 关键字**(string+tag 语法糖),不新增类型 | 零类型系统波及(避免 100+ 访问器文件);TextValidator/text-list 等机制直接复用;表头 `string`→`text` 即完成迁移 |
| 数据表示 | 加载期 DString,LoadDatas 末尾转换层改写为 **DInt(下标)** | 沿用 `TextKeyToValueTransformer`/`ProcessDatas` 既有模式;转换后 bin/json/checksum/行哈希全走现成 DInt 通路,零特判 |
| 下标分配 | **持久化 append-only 注册表**,住 sidecar;旧 key 保下标,新 key Ordinal 排序追加尾部,删除打墓碑不回收 | "排序位次"方案删一个 key 全体移位,违背幂等约束;墓碑保证下标永不漂移 |
| 运行态 string 访问 | **彻底取缔**(无 string→index 映射表文件) | 用户决策;~13 处 `GetOrDefault(string)` 调用点迁移(字面量→静态访问器;配置来源→随字段转 int) |
| 语言管线格式 | lang.conf 与 langAOT.conf 转数组格式(indexMode);**langServer 保持现有 string 字典格式** | 用户确认只有前两条用 string key;server 错误码文本体系不动 |
| 管线合并 | **Tier 2**:语言 schema 并入 game.conf,5 次调用收敛为 2 次(客户端 omnibus + 服务器);不做单 run 多 target(Tier 3) | 用户选定;静态语义已验证可行(无 tag 行不被 `-i` 滤掉、无 group 表进 default target、`-c` 多目标共存);注册表进程内构建,跨管线 artifact 整体消失 |
| 空 text 字段(非可空) | 下标 `-1` 哨兵 + WARN 日志,不阻塞迁移;`text?` 可空则正常 null | 待 review 确认 |
| convertTextKeyToValue | 与 indexMode 互斥,同开报错 | 语义冲突 |
| text 字段做表 key/index | indexMode 下禁止,转换期报错 | int key 语义混乱 |
| 首个版本迁移 | 必须走全量基准(bin 格式、字段类型、SignatureId 都变了,增量 gate 自然强制) | 天然安全阀 |

---

## 2. 总体架构(合并后)

```
┌─────────────────────── 基准导出(2 次 Luban 调用) ───────────────────────┐
│                                                                          │
│  [Run A] game.conf  -t client  -c cs-bin,cs-l10n-language  -d bin        │
│    dataExporter=omnibus-baseline                                        │
│    ├─ 普通表 → {out}/{table}.bytes + tables.json sidecar                 │
│    ├─ LanguageText(main space, indexMode)                                │
│    │    → {out}/language/{lang}/languageconfig.bytes  (数组格式)          │
│    │    → l10n.json sidecar(KeyEntries 注册表)                           │
│    ├─ LanguageAOT(aot space, indexMode)                                  │
│    │    → {out}/languageaot/{lang}/… + l10n.aot.json                     │
│    ├─ LanguageServer(server space, 旧格式)                               │
│    │    → {out}/languageserver/{lang}/…(string 字典,不变)                 │
│    ├─ text 字段 → LoadDatas 末尾进程内转换 DString→DInt(读各 space 注册表) │
│    └─ ChecksumConfig:表行 + per-language 行(per-space 文件)               │
│    代码:cs-bin(业务表 + 语言表类) + 3 个语言类(LanguageConfig 等)         │
│                                                                          │
│  [Run B] game.conf  -t server  -c java-json  -d json                     │
│    ├─ 业务表 → json(text 字段为 int,注册表同进程构建,不依赖 Run A)        │
│    └─ 语言表 → 普通 json(服务端可选自用;或按 group 排除)                  │
│                                                                          │
└──────────────────────────────────────────────────────────────────────────┘

┌─────────────────────── 增量导出(2 次 Luban 调用) ───────────────────────┐
│  [Run A'] -t client -d bin  dataExporter=omnibus-incremental            │
│    ├─ 普通表 → DLP1 patch(不变) + _delta.manifest                       │
│    ├─ main/aot space → LLP2 patch(全下标,无字符串)                       │
│    │    + 回写注册表到 sidecar(幂等关键)                                  │
│    └─ diff 基准仍是基准 sidecar 快照(累计语义,跳版本自愈,与现状一致)        │
│  [Run B'] -t server -d json(全量,现状不变)                               │
└──────────────────────────────────────────────────────────────────────────┘
```

要点:

- **注册表在每次 run 内自建自足**(sidecar 提供旧下标 + 语言表数据提供当前 key 集),Run A/B 之间无依赖、无顺序约束;
- 落盘布局与现状一致(语言文件多一层 space 前缀目录),客户端加载路径不变;
- 服务器 Java 侧只消费:业务表 json 的 int 字段 + 客户端 bin 拷贝(新数组格式),由服务端团队按 §7 格式文档适配。

---

## 3. 下标注册表(sidecar v2)

### 3.1 数据模型

`L10NSidecar`(Luban.Core/Incremental/SidecarModels.cs)扩展:

```csharp
public class L10NSidecar
{
    public string SignatureId { get; set; } = "";

    /// 新增:living 注册表。列表位置 == 全局下标。Deleted=true 为墓碑(槽位保留)。
    public List<KeyEntry> KeyEntries { get; set; } = new();

    /// 现有 Keys/Languages 语义迁移为"基准快照",仅基准运行时重写:
    /// Languages[lang].Hashes 与 KeyEntries 对齐(基准时刻);增量运行只追加
    /// KeyEntries、不动 Languages(保持"相对基准累计 diff"语义)。
    public List<string> Keys { get; set; } = new();        // 兼容读:旧格式
    public Dictionary<string, LangSidecar> Languages { get; set; } = new();
    public Dictionary<string, TableSidecarEntry> Tables { get; set; } = new();
}

public class KeyEntry
{
    public string Key { get; set; }
    public bool Deleted { get; set; }   // 墓碑
}
```

### 3.2 分配算法(所有 run 统一,幂等核心)

```
输入:prevEntries(上次 sidecar;无/旧格式 → 空),curKeys(本次语言表全部 key,去重)
1. live = prevEntries 中 !Deleted 的 key 集合
2. 已有 key(在 prevEntries 中,含墓碑):下标 = 原位置;墓碑复活(Key 回填,Deleted=false,值照常导出)
3. 新 key = curKeys - live:按 Ordinal 排序后追加到列表尾部
4. 删除 key = live - curKeys:原位打墓碑(Deleted=true),槽位保留
5. 输出 newEntries;bin 数组/静态访问器下标/增量 patch 全部以 newEntries 位置为准
```

同一份源数据重复导出 → newEntries 逐字段一致 → **下标与产物字节幂等**。

关键不变量:

- 增量 run 结束必须**回写 sidecar 的 KeyEntries**(否则两次增量间新 key 追加排序可能交错,导致同一 key 下标漂移——这是幂等的唯一陷阱);
- 下标永不回收;唯一重置途径 = 删 sidecar + 重新基准(文档写明);
- `Languages`(基准快照)只在基准 run 重写;增量 patch 语义保持现状的"相对基准累计",跳版本发放自愈,新增后又删除的 key 与现状一样遗留孤儿槽(下个基准清理)。

### 3.3 注册表构建时机

`GenerationContext.LoadDatas()` 锁内末尾:

```
LoadDatas:
  加载全部表数据(含语言表)
  → 按 space 构建 L10NKeyIndex(读 sidecar + 收集当前 key → §3.2 分配)
  → text 字段转换层(DString → DInt)遍历改写
  → 置 _keyIndexReady
```

放锁内是因为 `ProcessCodeTarget`(cs-l10n-language 内部自行 `LoadDatas()`)与数据导出并行跑(Task.Run),必须保证任何路径拿到数据时下标已解析。

---

## 4. Luban 侧设计

### 4.1 space 配置模型(多 key space)

现有单值 l10n 选项(lang.conf 里的 `l10n.languages` 等)升级为按 space 分组的扁平 key:

```
-x l10n.spaces=main,aot,server
-x l10n.main.tables=LanguageText            # 语言表名单(逗号分隔,可多表合并)
-x l10n.main.indexMode=true                 # 数组格式 + text→int
-x l10n.main.languages=zh_CN,en_US,...
-x l10n.main.outputFile=languageconfig
-x l10n.main.outputDir=language             # 输出子目录(相对 bin.outputDataDir)
-x l10n.main.keyFieldName=key
-x l10n.main.keyFieldDesc=zh_CN
-x l10n.main.sidecar=Output\baseline\l10n.json
-x l10n.aot.tables=LanguageAOTText
-x l10n.aot.indexMode=true
-x l10n.aot.outputDir=languageaot
-x l10n.aot.sidecar=Output\baseline\l10n.aot.json
-x l10n.server.tables=LanguageServer
-x l10n.server.indexMode=false              # 保持 string 字典格式(现状行为)
-x l10n.server.outputDir=languageserver     # 与现路径一致
-x l10n.server.sidecar=Output\baseline\l10n.server.json
```

> 业务表 text 字段引用哪个 space:全局单选项 `l10n.textRefSpace=main`(当前业务表只引用 main;将来需要 per-field 指定再扩展)。

兼容:未配置 `l10n.spaces` 时,现有单值选项(`l10n.languages`/`l10n.textFile.path`/`dataExporter=l10n-bin-split` 等)行为完全不变——**对 slg 之外的用户零破坏**。slg 迁移到新分组选项。

`GenerationContext` 新增 `IReadOnlyList<L10NSpace> L10NSpaces`(名称、表名单、是否 indexMode、语言列表、注册表等),原 `L10NLanguages`/`L10NTextKeyFieldName` 单值属性保留为"默认单 space"视图,现有调用点(GetL10NKeyInfos、checksum 注入、增量导出器)逐个切换到 spaces 模型。

### 4.2 text→int 转换层

新组件 `Luban.Core/L10N/TextKeyIndexTransformer.cs`(`IDataFuncVisitor2<DType>`,模式照抄 `TextKeyToValueTransformer`):

- 触发条件:`l10n.spaces` 配置且存在 indexMode=true 的 space;
- 遍历全部表记录,`type is TString && type.HasTag("text")` 的字段:
  - 空值(非可空)→ `DInt(-1)` + WARN(表名/字段/行主键);
  - 空值(可空 `text?`)→ 保持 null;
  - key 在所属 space 注册表 → `DInt(index)`;**所属 space 判定**:业务表引用哪个 space?默认 main(`l10n.main.refByDefault=true` 或 `l10n.textRefSpace=main`);找不到 → 硬错误,带表名/记录路径/字段名/key 值;
  - key 在任何 space 都找不到 → 硬错误(提前暴露,优于运行期 KeyError);
- 转换在 LoadDatas 锁内(§3.3),之后校验、checksum、导出、代码生成看到的全是 DInt。

### 4.3 语言 bin 数组格式(indexMode)

`{varint count}{value[0]…value[count-1]}`:

- count = 注册表长度(含墓碑槽);墓碑槽/缺失语言值 = 空串 `0x00`;
- 所有语言文件 count 相同、下标对齐;
- 复用 `L10NBinarySplitDataExporter` 的表合并/查重逻辑(`ExportL10NMergedPerLanguage`),仅输出序列化改为按注册表顺序写数组(`SerializeDictionaryToBinary` → `SerializeArrayByIndex`)。

### 4.4 增量 patch LLP2

```
"LLP2"(4B) + string signatureId + varint upsertCount + {varint index, string value}*
         + varint deleteCount + {varint index}*
```

- magic 从 LLP1 升 LLP2(格式不兼容,客户端旧 ApplyDelta 直接拒绝);
- 全下标、无字符串;
- diff 逻辑:相对基准快照(sidecar.Languages.Hashes)按 (space, lang, 下标) 比对;新增 key → 所有语言 upsert;删除 key → 所有语言 delete;墓碑复活按 upsert 处理;
- 增量 run 末尾回写 sidecar.KeyEntries(§3.2);
- `_delta.manifest`(omnibus 单份,普通表条目 + 语言条目混排):语言条目 `Entry.Table` = 语言名(与各 space checksumconfig 行名一致,语言名不与表名冲突)。

### 4.5 组合数据导出器(omnibus)

```
[DataExporter("omnibus-baseline")]   → 基准:普通表 + 3 space 语言导出 + 双 sidecar + checksum
[DataExporter("omnibus-incremental")] → 增量:普通表 DLP1 + indexMode space LLP2 + 注册表回写
```

内部路由(全部复用现有类/静态方法,新类主要是编排):

| 表 | 路由 | 产物 |
|---|---|---|
| 不在 space 名单的表 | `BaselineWithSidecarExporter` / `IncrementalDataExporter` 现有逻辑 | 表 bin/DLP1 + tables.json |
| space 表 + indexMode=true | 数组格式(§4.3)/ LLP2(§4.4) | `{outputDir}/{lang}/{outputFile}.bytes` + space sidecar |
| space 表 + indexMode=false | `L10NBinarySplitDataExporter` 现有逻辑(string 字典) | 同现状(语言服务端) |
| ChecksumConfig | 表行 + per-space per-language 行,**每 space 在各自 outputDir 下输出自己的 `checksumconfig.bytes`** | 文件名/行名与现状完全一致,客户端三个加载器(ChecksumConfig / LanguageChecksumConfig / LanguageServer…)零改动 |

### 4.6 cs-l10n-language 多 space 代码生成

选项单值 → 按 space 分组(沿用 `{targetName}.{key}` 命名风格):

```
-x cs-l10n-language.spaces=main,aot,server
-x cs-l10n-language.space.main.className=LanguageConfig
-x cs-l10n-language.space.main.keyFlag=is_code
-x cs-l10n-language.space.aot.className=LanguageAOTConfig
-x cs-l10n-language.space.server.className=LanguageServerConfig
```

一个 run 输出 N 个类。模板 `language.sbn` 重写:

```csharp
public static string {{key}} => Get({{index}});   // 纯名 + 懒加载,index 从注册表烘焙
```

- 修复现仓库模板回归:`_` 前缀 + 急切 `= Get(...)` 会导致静态初始化顺序问题,且 ~640 处现存调用全部使用纯名;
- `__key_type` 等无用模板变量清理;
- `L10NKeyInfo` 增加 `Index` 字段,`EnumerateL10NKeys` 按 space 关联注册表;
- keyFlag/keyTable 过滤逻辑保留。

### 4.7 cs-bin / java-json 对 text 字段的 int 输出

indexMode 下(`GenerationContext` 新增只读标志),两个目标家族的类型访问器对 `TString + text 标签` 特判:

- cs-bin:字段类型 `int`,反序列化 `ReadInt()`(varint,与 WriteInt 对齐);涉及 TypeNameVisitor / BinaryUnderlyingDeserializeVisitor 等约 4~6 个访问器文件,模板不动;
- java-json:字段类型 `int`,JSON 数值;Java 侧访问器同理;
- 其他语言目标(go/py/ts/lua/cpp/rust…):不特判,遇到 indexMode + text 字段输出 string 字段声明并打 WARN(数据是 int,声明是 string,明确告知不支持);
- `StructureSignature` 用 `GetType().Name`(TString)→ text 与 string 同签名;但字段 `string→text` 迁移会因 Tags 不进签名而**不**变签名——增量 gate 不强制重基准。**处理**:StructureSignature 的字段行追加 tags 输出(text 标签显式入签),保证类型迁移必触发重新基准(见 §8 风险表)。

### 4.8 不改动的部分

- `text` 关键字语法糖、TextValidator、DefaultTextProvider(`l10n.textFile.*`)、text-list 导出:原样复用;
- langServer 的 string 字典 bin 与 LLP1 行为(经 indexMode=false 路由);
- DLP1 普通表增量、tables.json、结构 gate;
- 普通表 cs-bin/java-json 代码生成。

---

## 5. 配置工程侧(D:\work\slg\common\config)

### 5.1 schema 合并

- `Tools/game.conf` 的 `schemaFiles` 追加 3 个语言 schema(`language_l10n.xml` / `language_l10n_server.xml` / `languageAOT_l10n.xml`),原 3 个语言 conf 保留一段时间作回退,验收后删除;
- 语言表进 game.conf 后自动属于 default group(`-t client`/`-t server` 均含;`-t test` 不含——无影响);
- `LanguageText` 保持无 group;若要排除 editor 可补 `&group=c`。

### 5.2 bat 重写(5 次调用 → 2 次)

基准:

```
[1] client omnibus:  -t client -c cs-bin,cs-l10n-language -d bin
    -x dataExporter=omnibus-baseline
    -x bin.outputDataDir=Output\Client\configs
    -x l10n.spaces=main,aot,server …
    -x incremental.sidecarPath=Output\baseline\tables.json
[2] server:          -t server -c java-json -d json(现状参数,语言表 json 一并输出)
```

增量:

```
[1] client omnibus:  -t client -d bin -x dataExporter=omnibus-incremental …
[2] server 全量 json(现状不变)
```

- 原先复制 configs\language → 服务端 classpath 的步骤保留(布局不变);
- STAMP 机制不变。

### 5.3 表头迁移(string → text)

- 逐字段 opt-in:`Item.csv` 的 `nameId/descriptionId` 等改为 `text&group=c`;
- 分批推进:第一批迁 Item + 高频引用表验证链路,其余跟进;
- 每批迁移后该表 SignatureId 变化(§4.7 已保证)→ 增量 gate 要求该表重新基准,天然安全;
- 语言表自身 `key` 字段保持 `string`(它是 key 之源,不参与转换)。

---

## 6. 客户端运行时(D:\work\slg\client\game)

### 6.1 LanguageConfig 重写(手写 partial)

```csharp
public partial class LanguageConfig
{
    private static string[] dataArr;          // 基准加载;ApplyDelta 需扩容时换数组
    public LanguageConfig(ByteBuf buf) {      // 数组格式 {count}{value*}
        int n = buf.ReadSize();
        dataArr = new string[n];
        for (int i = 0; i < n; i++) dataArr[i] = buf.ReadString();
    }
    public static string Get(int index)       // 静态访问器与业务 int 字段统一入口
        => (uint)index < (uint)dataArr.Length ? dataArr[index] : "";
    public void ApplyDelta(ByteBuf buf) { … } // LLP2:upsert 扩容/覆写;delete 置空串
}
```

- 删除 `GetOrDefault(string)` / `this[string]` / `DataMap`;
- `Tables.LoadLanguage(ByteBuf)` 签名不变;
- AOT 加载器(LanguageAOT 代码)同步数组化;LanguageServerConfig 保持 string 字典(格式未变)。

### 6.2 调用点迁移(~13 处 GetOrDefault)

- 字符串字面量(`"mail_delete_confirm"` 等)→ 改静态访问器 `LanguageConfig.mail_delete_confirm`;
- 配置表来源(guide/dialog 的 `data.Name/data.Text` 等)→ 随源表字段迁 `text` 后天然是 int,改 `Get(int)`;
- 编辑器工具(LauncherEditor 等)个别处理;
- ~640 处静态属性调用**零改动**(重新生成即用)。

### 6.3 ConfigSync / ApplyLanguageDelta

- LLP2 校验逻辑:magic 检查改为 LLP2,signatureId 比对保持;
- 客户端本地不持久化 patch(现状语义不变)。

---

## 7. 服务端交付物(文档,不在代码范围)

给服务端团队的格式说明:

1. 业务表 json:原 string 语言 key 字段 → **int 下标**(与客户端 bin 同一下标空间);
2. 客户端 language bin(拷贝到 classpath 的 `configs/client/language/`):新数组格式 `{varint count}{value*}`;
3. LLP2 patch 格式(§4.4);
4. 下标空间唯一权威 = main space 注册表(sidecar `l10n.json` 的 KeyEntries)。

---

## 8. 风险与开放问题

| # | 风险/问题 | 处理 |
|---|---|---|
| 1 | sidecar 丢失(Output 清理)→ 注册表重置 → 下标重排 | 与现有增量 sidecar 同生命周期;文档写明"删 sidecar 必须跟全量基准";可选加防呆:注册表重置时若检测到旧 KeyEntries 非空且 key 集合连续,打醒目 WARN |
| 2 | 两次增量间新 key 排序交错导致下标漂移 | 增量 run 强制回写 KeyEntries(§3.2 不变量) |
| 3 | StructureSignature 不含 tags → string→text 迁移不触发重基准 | 字段签名追加 tags(§4.7);影响:其他用户加 tag 也会 bump 签名——更保守,可接受 |
| 4 | `-t editor`/`-t test` 下语言表存在但 text 字段转换依赖 main space | editor 不配 spaces → 转换层跳过,字段保持 string(编辑器友好);test 不含语言表,如有 text 字段则硬报错提示 |
| 5 | omnibus 导出器路由误判(普通表恰好有语言列同名) | space 表用显式名单(`l10n.{space}.tables`),不做特征自动判定 |
| 6 | ChecksumConfig 三个 space 语言名撞车(zh_CN×3) | 已消解:每 space 在自己 outputDir 下输出 checksumconfig.bytes,同名不同目录,行名保持语言名(§4.5) |
| 7 | server run 语言表 json 输出量 | LanguageText 全语言列进 server json(约 14 列);若不需要,给语言表打 `&group=c` 排除 server |
| 8 | 空 text 字段语义(-1 哨兵 + WARN) | 待 review 拍板(也可改硬报错) |
| 9 | cs-bin 会为语言表生成 Tables 属性/bean 类 | 客户端 Tables 手写加载补一行;或后续用 outputTable 过滤 |

## 9. 验证方案(无自动化测试,走导表流水验证)

1. **幂等**:同一份源数据连续两次 omnibus-baseline → 两次的 languageconfig.bytes / LanguageConfig.cs / sidecar KeyEntries 逐字节一致;
2. **追加稳定**:加 key → 增量 → 校验新 key 下标 = 基准 count + 追加序;再跑一次增量 → 全部下标不变、无 patch 产出(内容未变);
3. **墓碑**:删 key → 增量 delete 指令正确、槽位保留;复活 → upsert;
4. **text→int**:Item.csv 迁 2 个字段 → bin/json 中为 varint 下标,未知 key 报错信息含表/行/字段;
5. **LLP2**:构造文案变更 → patch 应用后 `Get(n)` 与基准重导一致;扩容(追加 key)路径;
6. **checksum**:per-language 文件 MD5 与实际产物一致;Stamp gating 不回归;
7. **代码**:客户端工程编译通过;~640 静态调用零改动验证(抽查);Java json 字段类型 int;
8. **服务器 json**:Run B 独立跑通(不依赖 Run A 先执行)。

## 10. 实施顺序(供 writing-plans 细化)

1. sidecar v2 模型 + KeyEntries 分配器(Core,纯函数可独立验证);
2. 转换层 + LoadDatas 时序 + StructureSignature tags;
3. cs-bin/java-json text int 特判;
4. 数组 bin + LLP2 + omnibus 导出器(基准/增量);
5. cs-l10n-language 多 space + 模板重写;
6. spaces 配置模型 + GenerationContext 切换;
7. game.conf 合并 + bat 重写(配置仓);
8. 客户端运行时(LanguageConfig/ApplyDelta/AOT/调用点迁移);
9. 表头迁移第一批 + 全量基准 + 灰度验证;
10. CHANGELOG(按仓库规约,随代码同 commit)+ 服务端格式文档。
