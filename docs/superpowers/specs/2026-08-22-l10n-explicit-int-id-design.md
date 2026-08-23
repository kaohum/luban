# 多语言显式 int 语言 id 设计(v2 终态)

- 日期:2026-08-22
- 状态:待 review(用户)
- 作者:陈昊然 / Claude
- 前置文档:`2026-08-21-l10n-int-index-merged-pipeline-design.md`(v1,string key 锚点方案——其管线合并/增量框架/服务端文档条款继续有效;**语言 id 形态、存储格式、注册表机制条款被本文取代**)

---

## 1. 背景与策略演进

v1 已落地的形态:策划填 string key → 导表期自动分配下标 → 数组 bin。遗留断点:**FairyGUI i18n 插件的 customData 在运行时是 string langId**,string→index 映射已被取缔,UI 编辑器层无法换算(421 处 `i18n&<string key>`)。

v2 策略(用户拍板):**语言 id 改为显式 int,与 server 表同模式**(server 语言表 `id` 列本就是 int,运行时 `Dictionary<int,string>`)。string key 层从填写端起彻底消失,FairyGUI 断点自然消解。

### 1.1 决策记录(全部用户确认)

| # | 决策 | 内容 |
|---|---|---|
| D1 | 语言 id 形态 | 显式 int,策划直接填;**不再自动分配**(id 在表内,天然稳定幂等) |
| D2 | id 段分类 | 万级分段(见 §2),段内连续分配,id 永不复用(删行=墓碑) |
| D3 | 存储格式 | **int 键紧凑字典**(禁止稀疏数组):bin=`{count}{(varint id,string value)}*`(即 server space 现行格式);运行时=**`PooledHashMap<int,string>`**(客户端 `Assets/Plugins/Collections/`) |
| D4 | 配置表引用列 | **沿用 `text` 类型**(不新造关键字);单元格填 int id(字符串形式);**导表阶段静态检查**(id∈语言表;空=-1;缺失=告警+-1 并收集清单,不阻塞、不改语言表——沿用 R12 语义) |
| D5 | 字段命名规范 | 语言引用字段统一 `*Id`(name→nameId、desc→descId、text→textId、title→titleId、heroName→heroNameId…);已带 Id 的不动;CSV 表头+生成代码+客户端调用点联动 |
| D6 | FairyGUI | customData 填 `i18n&<int id>`;钩子 `int.TryParse → Get(id)`;存量 421 处一次性二进制改写(带逐包校验);27 个未注册 langId 保持直显并入清单 |
| D7 | 访问器命名 | 语言表 `name` 列(原 string key)作为静态访问器名与人读标识;`name => Get(10034)` |
| D8 | AOT | 同样 int 化,独立 id 空间(100000–199999 段),独立 bin |
| D9 | server space | 本就是 int id,**原样不动** |

## 2. 语言 id 段分类

| 段 | 模块(现语言表文件) | 现 key 数 | 余量 |
|---|---|---|---|
| 10000–19999 | 通用系统 GlobalCommon | 249 | ~40× |
| 20000–29999 | 城建 Building | 228 | ~44× |
| 30000–39999 | 战斗养成 BattleGrowth | 333 | ~30× |
| 40000–49999 | 玩法任务 GameplayQuest | 155 | ~64× |
| 50000–59999 | 大地图行军 WorldMapMarch | 145 | ~69× |
| 60000–69999 | 联盟社交 AllianceSocial | 325 | ~31× |
| 70000–79999 | 剧情引导 StoryGuide | 99 | ~100× |
| 80000–89999 | 道具邮件 ItemMail | 382 | ~26× |
| 90000–99999 | 预留 | — | — |
| 100000+ | 十万级扩展区(SDK/运营/特殊系统) | — | — |
| 100000–199999 | AOT 空间(独立 bin,独立分配) | 30 | — |

规则:段内从段首连续分配;新行取所在段下一个空位;删除打墓碑(id 不复用);段满顺延 90000 段并记录。

## 3. 终态架构

```
语言表:   id(int,分段,表主键) + name(string,访问器命名/人读) + ##类型 + ##描述 + is_code + 14 语言列
配置表:   语言引用列 = text 类型,单元格填 int id;导出静态检查(id∈表/空=-1/缺失告警)
bin:      {varint count}{(varint id, string value)}* —— int 键紧凑字典(server space 现行格式)
运行时:   LanguageConfig = PooledHashMap<int,string>
             Get(int id) → TryGetValue,miss 返回空串
             ApplyDelta(LLP2) → upsert: map[id]=value;delete: map.Remove(id)
增量:     LLP2 全 id 键(格式不变,语义 id 即键);sidecar 记 id 集+墓碑+per-language 哈希
代码生成:  静态访问器 `public static string {name} => Get({id});`(id 烘焙;is_code 过滤不变)
          ToLanguageText(this int) 不变;cs-bin/java-json int 特判机制不变
FairyGUI: customData `i18n&10034`;GetLanguageText = s => int.TryParse(s,out var i) ? Get(i) : s
```

## 4. Luban 侧改造(对已实现的 v1 机制)

| v1 机制 | v2 改造 |
|---|---|
| text 转换层(string key→查注册表→DInt 下标) | **单元格为 int 字面量**:parse int + 静态校验 id∈id 集→DInt;parse 失败/id 缺失→告警+-1(R12 语义);空→-1 |
| KeyIndexAllocator(排序自动分配) | **退役**。注册表=语言表 id 集合(HashSet<int>);墓碑由 sidecar 记录(增量 diff 用) |
| 数组 bin 序列化(SerializeLanguageArray) | **退役**。main/aot space 走 int 字典导出路径(server space 现行 ExportL10NMergedPerLanguage 机制,keyType=TInt) |
| sidecar KeyEntries:{Key:string,Deleted} | **{Id:int,Deleted}**;基准快照 Keys→int 列表 |
| LLP2 | 格式不变(magic+sig+upserts+deletes);字段语义从"数组位置"变"id" |
| 语言表 key 字段(string) | id(int)+name(string);表 index 改 id 列;key 读取处 DString→DInt |
| EnumerateL10NKeys(key=访问器名+下标回填) | id 列→Get(id) 烘焙;name 列→访问器名 |
| indexMode space 区分 | 三 space 格式统一(int 字典),indexMode 分支简化为同一路径 |
| cs-l10n-language 模板 | Get(int) 移到手写 partial(字典查);生成侧只出访问器 |

## 5. 一次性数据迁移(脚本化)

1. **id 分配**:按 §2 段表给 1916 key 分配 id;产出新语言表 CSV(id,name,类型,描述,is_code,14 语言列)+ `old-key→id` 映射表(存档备查)
2. **语言表 schema**:`language_l10n.xml`/`languageAOT_l10n.xml` 的 key var→`id int` + `name string`;table index field→id
3. **引用列改写**:64 列×46 表单元格 string key→int id;**字段名 *Id 规范化**(表头+##type 行);673 个未注册 key→`-1`+并入缺失清单(按段给建议 id)
4. **FairyGUI 存量**:421 处 `i18n&<key>`→`i18n&<id>`(_fui.bytes 长度感知二进制安全替换+逐包校验;27 未注册保持直显)
5. **sidecar 重置**:删旧 l10n sidecar,首次全量基准(id 语义变化强制重基准)
6. **缺失 key 清单**:`docs/missing-language-keys.md` 改版——按段列出建议 id,开发补行时直接取用

## 6. 客户端侧改造

| 项 | 内容 |
|---|---|
| LanguageConfig 手写 partial | dataArr→`PooledHashMap<int,string> dataMapRef`(Create 池化);构造读 {count}{(id,value)*};`Get(int)` 字典查;`ApplyDelta` LLP2(upsert/remove);`Count` |
| 生成侧模板 | 只出访问器 `name => Get(id)`;Get 移手写 |
| MarchCoreTests | 桩改 PooledHashMap 注入(id→文案) |
| UpgradeLocalization(AOT) | 数组+baked sKeyIndex→PooledHashMap 直查;GetText(string)→GetText(int)(31 处调用点改传 id) |
| UIManager 钩子 | `int.TryParse → Get(id)`(替换 TODO 直显) |
| 调用点 | *Id 改名联动(编译器驱动修复);其余已传 int 的调用点零改动 |

## 7. 验证方案

1. **幂等**:同数据两次全量基准→bin/sidecar/代码逐字节一致(id 在表内,天然稳定)
2. **增量**:加行(新 id)→LLP2 upsert 该 id;删行→delete;改文案→upsert;重跑无变化→空 delta
3. **静态检查**:引用列填不存在 id→告警+该格 -1;空→-1;清单汇总
4. **编译门**:Game.Runtime / Game.Config.Runtime 0 错误;*Id 改名后调用点全通
5. **FairyGUI**:改写后逐包二进制结构校验+UI 冒烟(确定/取消等按钮各语言显示)
6. **server space**:零差异(不动)

## 8. 风险与开放问题

| # | 风险 | 处理 |
|---|---|---|
| 1 | FairyGUI 二进制一次性改写 | 长度感知替换+改写前后逐包结构校验;保留原包备份;设计后续在编辑器直接填 int |
| 2 | *Id 改名波及服务端 Java 字段名 | 服务端格式文档更新(字段名变更清单);服务端团队适配 |
| 3 | id 误填跨段/重复 | 导表静态检查:重复 id=硬错误;跨段=告警 |
| 4 | v1 的数组/自动分配代码退役不彻底 | Plan 3 内明确删除清单,避免死代码 |
| 5 | 旧增量 patch(int=旧下标)与新 id 混淆 | 迁移即全量基准,旧 patch 作废;版本号/签名天然隔离 |
