# 多语言显式 int 语言 id 迁移 — Plan 3 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 语言 id 从 string key(导表期自动分配下标)迁移为**显式 int**(万级分段,与 server 表同模式);存储改 int 键紧凑字典(PooledHashMap);字段名 *Id 规范化;FairyGUI customData 直填 int;全链路(配置表/代码/UI)统一 int key。

**Architecture:** 复用 v1 已交付的管线合并/omnibus/LLP2/增量框架与客户端 int 调用点形态;改造 text 转换层(int 解析+静态校验)、注册表(id 集)、sidecar(int)、语言表 schema(id+name)、运行时(PooledHashMap);退役数组序列化与自动分配器。

**Tech Stack:** Luban(E:\Projects\luban)、配置仓(D:\work\slg\common\config)、客户端(D:\work\slg\client\game)、迁移脚本(Python)。

**Spec:** `docs/superpowers/specs/2026-08-22-l10n-explicit-int-id-design.md`(权威;取代 v1 spec 的 id 形态/存储/注册表条款)

## Global Constraints

- **三仓全程零 commit**(用户验收后统一提交,含 CHANGELOG)。
- 构建链:Luban `dotnet build Luban.sln` → PostBuild 自动部署 DLL 到配置仓 Tools/Luban;**部署模板目录(Tools/Luban/Templates)手工同步**(R10 教训)。
- 客户端编译门:Game.Runtime / Game.Config.Runtime 0 错误;Assembly-CSharp 以 Unity Editor 编译为准(人工验收)。
- server space(languageServer)零改动、零差异。
- 语言表内容(文案翻译)不新增不修改——673 个缺失 key 仅产出清单(按段给建议 id)。
- id 段表(spec §2)为分配依据;id 一经分配写入新语言表 CSV 即冻结。
- 字段命名规范:语言引用字段统一 `*Id`;已带 Id 后缀的不动。

## 关键跨任务接口

```csharp
// T1 产出(改造)
L10NKeyIndexBuilder:
  - 注册表 = HashSet<int> CurrentIds(语言表 id 列活 id 集)+ sidecar 墓碑 id 集
  - text 转换: DString(单元格里是 int 字面量)→ parse → id∈集 ? DInt(id) : (warn + DInt(-1))
  - 重复 id 硬错误;跨段 id 告警
// T2 产出
SidecarModels: KeyEntry { int Id; bool Deleted; }  L10NSidecar.Keys : List<int>
LLP2: 布局不变,字段语义 = id
omnibus: main/aot/server 统一走 int 字典导出路径
// T3 产出
language.sbn: 只出访问器 `public static string {{name}} => Get({{id}});`(Get 移手写 partial)
// T4 产出
Output/migration/key-to-id.csv(old-key→id 映射,存档)+ 新语言表 CSV(id,name,类型,描述,is_code,14 语言列)
// T7 产出(客户端运行时契约)
LanguageConfig 手写 partial:
  PooledHashMap<int,string> dataMapRef(生成侧访问器引用)
  public static string Get(int id) → TryGetValue/空串
  ApplyDelta(ByteBuf) LLP2: upsert map[id]=v;delete map.Remove(id)
```

---

### Task 1: Luban 语言表 int 键机制 + text 转换层改造

**Files:**
- Modify: `src/Luban.Core/L10N/L10NKeyIndexBuilder.cs`(核心:CollectCurrentKeys 读 DInt id;TransformTextFields 改 parse+校验;重复/跨段检查)
- Modify: `src/Luban.Core/L10N/TextKeyIndexTransformer.cs`(DString→parse int→DInt;parse 失败/id 缺失→warn+-1;空→-1)
- Modify: `src/Luban.Core/Incremental/KeyIndexAllocator.cs`(**退役删除**;墓碑判定逻辑并入 builder:活 id 集 = 表内 id ∪(sidecar 墓碑差集))
- Test: `src/Luban.Tests/`(改造 KeyIndexAllocatorTests→id 集校验测试;TextKeyIndexTransformerTests→parse/缺失/空三态)

**要点:**
1. 语言表 id 列是 `int`(T5 改 schema 后);所有读 key 字段处 `as DString`→`as DInt`(L10NChecksumUtil.BuildPerLanguageMap、L10NBinarySplitDataExporter 合并导出、GenerationContext.EnumerateL10NKeys 等,grep `KeyFieldName` 全量排查)。
2. text 单元格内容是 int 字面量字符串("10034");转换层不再查 string 注册表。
3. 静态检查三态:合法 id→DInt(id);空→DInt(-1)+WARN;parse 失败或 id∉集→DInt(-1)+`[lan-index][missing-id]` WARN+收集(汇总清单,沿用 R12 通道)。
4. 语言表自身重复 id→硬错误;id 跨段(对照 spec §2 段表,硬编码段边界)→WARN。
5. convertTextKeyToValue 互斥守卫、text 做表 key 守卫:保留;text 做表 key 守卫语义变化(text 现在转 int)——校验 index 字段为 text 仍报错 ✓ 不变。

- [ ] TDD:转换层三态测试先行
- [ ] 实现 + `dotnet test Luban.Tests` 绿 + `dotnet build Luban.sln` 0 错
- [ ] mini conf 改造(schema id int+name string,lang.csv 加 id 列)冒烟:合法/缺失/空三态 + 重复 id 硬错

### Task 2: sidecar int 化 + LLP2 id 语义 + omnibus 统一 int 字典路径

**Files:**
- Modify: `src/Luban.Core/Incremental/SidecarModels.cs`(KeyEntry.Id:int;Keys:List<int>)
- Modify: `src/Luban.DataTarget.Builtin/Incremental/IncrementalL10NDataExporter.cs`(HandleSpace:键全 int;diff/墓碑/回写按 id)
- Modify: `src/Luban.DataTarget.Builtin/Incremental/L10NBaselineWithSidecarExporter.cs`(sidecar 写入 int)
- Modify: `src/Luban.DataTarget.Builtin/Incremental/OmnibusBaselineExporter.cs` / `OmnibusIncrementalExporter.cs`(main/aot 改走 int 字典导出=legacy 路径;`ExportL10NArrayPerLanguage` 调用点移除)
- Modify: `src/Luban.Core/Incremental/L10NChecksumUtil.cs`(**删除 SerializeLanguageArray**;ComputePerLanguageFileMd5 走 int 字典序列化)
- Modify: `src/Luban.DataTarget.Builtin/L10NBinarySplitDataExporter.cs`(删除数组导出方法;合并导出 keyType=DInt 分支即主路径)

**要点:**
1. int 字典 bin:`{varint count}{(varint id, string value)}*`——即现有 SerializeLanguageBytes(TInt) 路径(server space 同款),main/aot 复用。
2. LLP2 布局不变;语义注释更新(字段= id)。
3. 墓碑:基准快照(表内 id+哈希)+ KeyEntries(Id,Deleted);增量回写不变量保留。
4. **幂等**:同数据两次基准逐字节一致(id 在表内,不再依赖分配顺序)。
5. sidecar 兼容:旧 string KeyEntries 读入→视为空(迁移即全量基准,无兼容负担)。

- [ ] mini conf 基准+增量冒烟:加行(新 id)→upsert;删行→delete;改文案→upsert;幂等重跑
- [ ] `dotnet test` 绿 + build 0 错

### Task 3: cs-l10n-language v2(id 烘焙 + name 命名 + Get 移手写)

**Files:**
- Modify: `src/Luban.CSharp/Templates/cs-l10n-language/language.sbn`(只出访问器:`public static string {{ k.field_name }} => Get({{ k.id }});`;删除生成侧 Get(int) 与 dataArrRef 引用——改引 `dataMapRef`)
- Modify: `src/Luban.CSharp/CodeTarget/CsharpL10NLanguageCodeTarget.cs`(访问器名=name 列;Get(id) 烘焙用 id 列;desync 守卫:name 行的 id 不在 id 集→抛;legacy/intMode 双形态简化——三 space 统一 int 字典后模板单形态)
- Modify: `src/Luban.Core/L10NKeyInfo.cs`(Key→Id:int + Name:string)
- Modify: `src/Luban.Core/GenerationContext.cs`(EnumerateL10NKeys:id 列→Id,name 列→FieldName)

- [ ] mini conf 冒烟:访问器 `key1 => Get(10001)` 形态;XML 注释取 name/描述列
- [ ] 部署模板同步(Tools/Luban/Templates/cs-l10n-language/language.sbn)

### Task 4: id 分配脚本 + 新语言表(可与 T3 并行)

**Files:**
- Create: `D:\work\slg\common\config\Tools\migrate_l10n_ids.py`(分配脚本,存档备查)
- Create: `Tables/本地化/LanguageTextXX_*.csv` 新版(id,name,##类型,##描述,is_code,14 语言列)
- Create: `Output/migration/key-to-id.csv`(old-key→id 映射,存档)

**要点:**
1. 按 spec §2 段表分配:每文件段内从段首连续;产出保持原列序(key 列位置换成 id+name 两列)。
2. 映射表含全部 1916 条;AOT 表同脚本处理(100000 起)。
3. **不迁移文案内容**;is_code/类型/描述列原样平移。
4. 校验:分配后无重复 id;每段用量报告。

### Task 5: 配置仓改写(xml + 引用列 + *Id 规范化)

**Files:**
- Modify: `Tools/language_l10n.xml` / `languageAOT_l10n.xml`(key var→`id int` + `name string`;index field→id)
- Modify: 64 列×46 表 CSV(单元格 key→id,用 T4 映射;未注册→`-1`)
- Modify: 同批 CSV 表头(字段名 *Id 规范化:name→nameId、desc→descId、text→textId、title→titleId、heroName→heroNameId…;已带 Id 不动;**列名改动的完整清单在执行时从 T8/T8b 迁移记录推导并列入报告**)
- Modify: `Tools/game.conf` 无需结构变化(spaces 配置沿用;确认 keyFieldName→id)
- Create: `docs/missing-language-keys.md` 改版(按段列建议 id)

**要点:**
1. 删除旧 l10n sidecar(Output/baseline/l10n.json、l10n.aot.json)→ 首次全量基准。
2. 重命名字段会改变表结构签名→增量 gate 自然要求全量(符合预期,一次性)。
3. 导出验证:exit 0;main/aot bin=int 字典;languageServer 零差异;幂等重跑。

### Task 6: FairyGUI 存量改写(可与 T5 并行,依赖 T4)

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Publish\ui\*_fui.bytes`(421 处 `i18n&<old-key>`→`i18n&<id>`)
- Create: 改写脚本 + 改写报告(逐包前后校验)

**要点:**
1. 长度感知替换:customData 为长度前缀 UTF-8 串;定位 `i18n&` 载荷→替换 key→id→修正长度前缀→重写文件。
2. 逐包校验:改写后重扫全部 i18n& 标记=合法 int;包结构完整性(FairyGUI 加载冒烟留 T9)。
3. 27 个未注册 langId 保持原样(运行时直显兜底)。
4. 改写前整目录备份(`Output/migration/fui-backup/`)。

### Task 7: 客户端 LanguageConfig PooledHashMap

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Scripts\Configs\LanguageConfig.cs`(手写 partial:PooledHashMap<int,string> dataMapRef;构造读 {count}{(id,value)*};Get(int);ApplyDelta LLP2 upsert/remove;Count)
- Modify: `Assets/Scripts/Game/Editor/March/MarchCoreTests.cs`(桩改 PooledHashMap 注入)

**契约:** 生成侧访问器 `=> Get(id)` 引用 Get 与 dataMapRef——Get 由手写侧提供(模板已移除生成侧 Get,T3)。

### Task 8: AOT int 字典化

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Scripts\AOT\Runtime\Framework\Upgrade\UpgradeLocalization.cs`(数组+baked sKeyIndex→PooledHashMap<int,string> 直查;bin=int 字典)
- Modify: 31 处 `GetText("<key>")` 调用点→`GetText(<id>)`(用 T4 映射)

### Task 9: 钩子 + 调用点联动 + 编译门

**Files:**
- Modify: `UIManager.cs`(`GetLanguageText = s => int.TryParse(s, out var i) ? LanguageConfig.Get(i) : s`,替换 TODO 直显)
- Modify: *Id 改名波及的全部调用点(编译器驱动:build→按错误清单机械修复)
- 验证:Game.Runtime / Game.Config.Runtime 0 错误;grep 清(GetOrDefault=0、ToLanguageText(string)=0、TODO(l10n-int) 仅剩 sanctioned 清单)

### Task 10: 全链验证 + 文档

1. 幂等(两次基准逐字节)/ 增量(加/删/改文案→LLP2 对应 id)/ 静态检查三态 / server space 零差异
2. FairyGUI UI 冒烟(确定/取消按钮多语言)
3. `docs/l10n-int-index-format.md` 更新:int 字典 bin、*Id 字段名变更清单(服务端适配)
4. 缺失 key 清单(按段+建议 id)终版
5. VERIFY.md 汇总

---

## 分组(组间串行,组内并发)

- G1: [T1] → G2: [T2] → G3: [T3] ∥ [T4] → G4: [T5] ∥ [T6] → G5: [T7] ∥ [T8] → G6: [T9] → G7: [T10]

## Self-Review

- Spec 覆盖:D1–D9 全部落任务(T1=D4、T2=D3 存储与增量、T3=D7、T4/T5=D1/D2/D5、T6=D6、T7/D3、T8=D8、D9=server 不动贯穿验证)✓
- 退役清单内联:T1(分配器)、T2(数组序列化)、T3(生成侧 Get/双形态)✓
- 接口一致性:dataMapRef/Get(id)/KeyEntry.Id/key-to-id.csv 跨任务签名一致 ✓
