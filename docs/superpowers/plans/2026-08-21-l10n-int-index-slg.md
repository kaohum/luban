# 多语言 int 下标改造 — Plan 2:配置仓合并 + 客户端运行时 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把 3 条语言管线并入 game.conf(导出从 5 次 Luban 调用收敛为 2 次),客户端 LanguageConfig/AOT 加载器切换到数组格式 + LLP2,迁移首批 text 字段与全部 `GetOrDefault(string)` 调用点。

**Architecture:** 依赖 Plan 1(`docs/superpowers/plans/2026-08-21-l10n-int-index-luban.md`)全部完成并构建——Luban 的 PostBuild 会把新 DLL 自动部署到 `Tools\Luban`。配置仓改 conf/bat/表头;客户端改手写 partial(数组 `dataArrRef` + `Get(int)`,与 Plan 1 Task 12 生成的模板类拼接)。

**Tech Stack:** Luban CLI(新 DLL)、C#(Unity 客户端)、bat/PowerShell。

**Spec:** `docs/superpowers/specs/2026-08-21-l10n-int-index-merged-pipeline-design.md`(§2 架构、§5 配置侧、§6 客户端、§7 服务端文档;§3/§4 由 Plan 1 实现)

## Global Constraints

- **前置**:Plan 1 全部任务完成、`dotnet build Luban.sln` 通过(DLL 自动部署进 `D:\work\slg\common\config\Tools\Luban`)。
- 客户端工程:`D:\work\slg\client\game`(Unity)。无命令行编译保证——优先 `dotnet build` Unity 生成的 `*.csproj`(若存在且可编),否则以 Unity Editor 编译 + Play 冒烟为验收。
- 三个 space 的事实(来自现状 conf,写死进 bat):
  - **main**:表 `LanguageText`,indexMode=true,14 语言,`outputFile=languageconfig`,`outputDir=language`,`keyFieldName=key`,`keyFieldDesc=zh_CN`,sidecar `Output\baseline\l10n.json`
  - **aot**:表 `LanguageAOT`,indexMode=true,14 语言,`outputFile=languageaotconfig`,`outputDir=languageaot`,sidecar `Output\baseline\l10n.aot.json`
  - **server**(legacy,不转格式):表 `LanguageServerConfig`,indexMode=false,14 语言,`outputDir=languageServer`,`keyFieldName=id`,sidecar `Output\baseline\l10n.server.json`
  - 14 语言列表:`zh_CN,en_US,zh_TW,id_ID,de_DE,fr_FR,es,pt_BR,th_TH,ru_RU,it_IT,ni_NI,pl_PL,sv_SV`
- 每次改动前用旧 bat 跑一份基准产物存档(`Output\legacy-对照\`),迁移后逐文件对比——**server space 与普通表必须零差异;main/aot 差异仅限语言 bin/patch 新格式**。
- 每任务一 commit;本仓(common/config)提交信息中文,遵循其 README 约定。

---

### Task 1: game.conf 合并三个语言 schema

**Files:**
- Modify: `D:\work\slg\common\config\Tools\game.conf`
- 不动: `Tools\lang.conf`、`Tools\langServer.conf`、`Tools\langAOT.conf`(保留作回退,验收后另行删除)

**Interfaces:**
- Produces: game.conf 含 3 个语言 schema 文件 + spaces xargs;`-t client`/`-t server` 均含语言表(default group)

- [ ] **Step 1: 存档旧基准产物(对照用)**

```powershell
cd D:\work\slg\common\config
./1基准配置导出.bat   # 用当前旧管线完整跑一次
Copy-Item -Recurse -Force Output\Client\configs Output\legacy-对照\configs
Copy-Item -Recurse -Force Output\baseline Output\legacy-对照\baseline
Copy-Item -Recurse -Force ..\..\client\game\Assets\Scripts\Configs\Language Output\legacy-对照\LanguageCode
Copy-Item -Recurse -Force ..\..\client\game\Assets\Scripts\Configs\LanguageServer Output\legacy-对照\LanguageServerCode
```

- [ ] **Step 2: 修改 game.conf**

`schemaFiles` 追加三项(放在现有文件之后):

```json
{"fileName":"language_l10n.xml", "type":""},
{"fileName":"language_l10n_server.xml", "type":""},
{"fileName":"languageAOT_l10n.xml", "type":""}
```

`xargs` 追加(注意 `l10n.*` 分组选项,与 spec §4.1 一致):

```json
"l10n.spaces=main,aot,server",
"l10n.textRefSpace=main",
"l10n.main.tables=LanguageText",
"l10n.main.indexMode=true",
"l10n.main.languages=zh_CN,en_US,zh_TW,id_ID,de_DE,fr_FR,es,pt_BR,th_TH,ru_RU,it_IT,ni_NI,pl_PL,sv_SV",
"l10n.main.outputFile=languageconfig",
"l10n.main.outputDir=language",
"l10n.main.keyFieldName=key",
"l10n.main.keyFieldDesc=zh_CN",
"l10n.main.sidecar=Output/baseline/l10n.json",
"l10n.aot.tables=LanguageAOT",
"l10n.aot.indexMode=true",
"l10n.aot.languages=zh_CN,en_US,zh_TW,id_ID,de_DE,fr_FR,es,pt_BR,th_TH,ru_RU,it_IT,ni_NI,pl_PL,sv_SV",
"l10n.aot.outputFile=languageaotconfig",
"l10n.aot.outputDir=languageaot",
"l10n.aot.sidecar=Output/baseline/l10n.aot.json",
"l10n.server.tables=LanguageServerConfig",
"l10n.server.indexMode=false",
"l10n.server.languages=zh_CN,en_US,zh_TW,id_ID,de_DE,fr_FR,es,pt_BR,th_TH,ru_RU,it_IT,ni_NI,pl_PL,sv_SV",
"l10n.server.outputDir=languageServer",
"l10n.server.keyFieldName=id",
"l10n.server.sidecar=Output/baseline/l10n.server.json"
```

> 注意:三个语言 schema 文件里的 `__intern__` 命名空间/bean 名与 game schema 无冲突(Language/LanguageServer/LanguageAOT bean 与 game 的 LanguageDef 表不重名);若跑基准时报 bean/table 重名,以报错为准调整(语言 xml 的 namespace 已是独立模块)。

- [ ] **Step 3: 冒烟——沿用旧 bat 先确认 schema 合并可解析**

```powershell
dotnet Tools\Luban\Luban.dll --conf Tools\game.conf -t client -c cs-bin -d bin -i dev test -f -x bin.outputDataDir=Output\smoke\configs -x dataExporter=default
```

Expected: 成功跑通(此步仅验证 schema 合并后可解析、数据可加载;`dataExporter=default` 下语言表会被当普通表导出,不管产物)。

- [ ] **Step 4: Commit(配置仓)**

```powershell
git add Tools/game.conf
git commit -m "feat: game.conf 合并三条语言管线 schema + l10n spaces 配置"
```

---

### Task 2: 基准 bat 重写 + 新旧产物对比

**Files:**
- Modify: `D:\work\slg\common\config\1基准配置导出.bat`

**Interfaces:**
- Consumes: omnibus-baseline 导出器(Plan 1 Task 10)、cs-l10n-language 多 space(Plan 1 Task 12)
- Produces: 2 次调用的新基准导出;落盘布局与旧版一致

- [ ] **Step 1: 重写 bat**

保持文件头部 chcp/变量定义/STAMP 不变,主体替换为(完整替换"客户端配置表导出"到"AOT localization export"五个阶段):

```bat
echo 正在导出客户端配置表(含多语言 omnibus)...
dotnet %LUBAN_DLL% ^
    -t client ^
    -c cs-bin -c cs-l10n-language ^
    -d bin ^
    -i dev test ^
    -f ^
    --conf %CONF_ROOT% ^
    -x cs-bin.outputCodeDir=%GEN_CODE_SOURCE% ^
    -x bin.outputDataDir=%GEN_DATA_SOURCE% ^
    -x dataExporter=omnibus-baseline ^
    -x outputCodeDir=..\..\client\game\Assets\Scripts\Configs\Language ^
    -x cs-l10n-language.space.main.className=LanguageConfig ^
    -x cs-l10n-language.space.server.className=LanguageServerConfig ^
    -x cs-l10n-language.space.server.outputDir=..\..\client\game\Assets\Scripts\Configs\LanguageServer ^
    -x cs-l10n-language.space.aot.className=LanguageAOTConfig ^
    -x incremental.sidecarPath=%SIDECAR_TABLES% ^
    -x incremental.exportStamp=%STAMP%
if %ERRORLEVEL% neq 0 (
    echo 客户端配置表导出失败,停止执行后续操作
    pause
    exit /b %ERRORLEVEL%
)
```

> 三个说明:
> 1. `cs-l10n-language.space.*.outputDir` 是 Plan 1 未定义的选项——**若 Plan 1 Task 12 未实现 per-space 输出目录,三个类会生成到同一个 outputCodeDir**。处理:在 Plan 1 Task 12 的 RenderSpace 里支持 `space.{name}.outputDir`(默认 outputCodeDir);此 bat 依赖它。执行 Plan 1 时若发现没做,回补(小改动:CreateOutputFile 前 拼 outputDir)。
> 2. server run 保持原参数不动(第二个 dotnet 调用,java-json),但**删掉其中的 `-x incremental.*`?** 不删——server run 仍需要 tables.json 的 stamp gating 做全量 json 输出?看原 bat:server 阶段带 `-x incremental.sidecarPath=%SIDECAR_TABLES%`(用于 stamp gating)。保留原样。
> 3. 原 bat 中 langServer/AOT 两个阶段的 `outputCodeDir` 分别是 `Configs\LanguageServer`(注意:是 `..\..\client\game\Assets\Scripts\Configs\LanguageServer`?原文件是 `outputCodeDir=..\..\client\game\Assets\Scripts\Configs\LanguageServer`——核对原 bat 第 4 阶段)与 `Output\Client\LanguageAOTCode`。新 bat 按上面对齐原路径。
> 4. 保留 bat 尾部全部复制步骤(xcopy Language 代码、basic+language → 服务端 classpath)不变;删除三个独立语言阶段的复制段(其产物已并入 configs)。

- [ ] **Step 2: 跑新基准 + 对比**

```powershell
./1基准配置导出.bat
```

对比断言(逐条执行):

```powershell
# 1. server space 零差异(文件名+字节)
diff -r Output\legacy-对照\configs\languageServer Output\Client\configs\languageServer
# 2. 普通表零差异
#    (对比 legacy-对照\configs 下除 language*/ 外的所有 .bytes;语言目录结构变了不比)
# 3. main/aot 新格式存在:
ls Output\Client\configs\language\zh_CN\languageconfig.bytes
ls Output\Client\configs\languageaot\zh_CN\languageaotconfig.bytes
# 4. sidecar 三份都在且 l10n.json 含 KeyEntries
Select-String -Path Output\baseline\l10n.json -Pattern KeyEntries
# 5. 语言代码三个类生成到位
ls ..\..\client\game\Assets\Scripts\Configs\Language\LanguageConfig.cs
```

Expected: 1/2 零差异;3/4/5 存在。任何普通表差异 → 停,回 Plan 1 排查(多半是 TableFilter 漏过滤或 checksum 注入行为变化)。

- [ ] **Step 3: 幂等重跑**

再跑一次 bat,对比 `Output\Client\configs` 与 sidecar 的 KeyEntries(Stamp 字段允许因 gating 沿用而相同)。

- [ ] **Step 4: Commit**

```powershell
git add 1基准配置导出.bat 1基准配置导出.sh
git commit -m "feat: 基准导出收敛为 omnibus 2 次调用(client+server)"
```

(若 `1基准配置导出.sh` 与 bat 并行维护,同步改;不维护则只改 bat 并在 commit 信息注明。)

---

### Task 3: 增量 bat 重写

**Files:**
- Modify: `D:\work\slg\common\config\2增量配置导出.bat`

**Interfaces:**
- Consumes: omnibus-incremental(Plan 1 Task 11)
- Produces: 客户端增量一次调用(DLP1 + main/aot LLP2)+ server 全量 json

- [ ] **Step 1: 重写 [2/4] 段为 omnibus**

```bat
echo [2/4] 客户端增量(普通表+多语言)
dotnet %LUBAN_DLL% -t client -d bin -i dev test -f --validationFailAsError --conf %CONF_ROOT% ^
    -x bin.outputDataDir=%OUT%\Delta\client ^
    -x dataExporter=omnibus-incremental ^
    -x incremental.sidecarPath=%SIDECAR% ^
    -x incremental.exportStamp=%STAMP%
if errorlevel 1 goto fail
```

> 原 [4/4] L10N 增量段删除(LLP2 已并入上式;langServer 无增量,现状如此)。`_l10n.delta.manifest` 与 `_delta.manifest` 在 omnibus 下合并为一份 `_delta.manifest`——检查服务器/工具脚本是否引用旧文件名(grep `Delta\` 与 manifest 的消费者),有则同步。

- [ ] **Step 2: 增量冒烟**

1. 基准后,改一个业务表值 + 一条语言文案 + 语言表加一个新 key(各一处);
2. 跑增量 bat;
3. 断言:`Delta\client\` 下有该表 `.patch.bytes`;`Delta\client\language\{lang}\languageconfig.patch.bytes` 存在且首 4 字节 = `LLP2`;`Delta.zip` 打包成功。

- [ ] **Step 3: Commit**

```powershell
git add 2增量配置导出.bat 2增量配置导出.sh
git commit -m "feat: 增量导出收敛为 omnibus(DLP1+LLP2 合并 manifest)"
```

---

### Task 4: 客户端 LanguageConfig 数组化(手写 partial)

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Scripts\Configs\LanguageConfig.cs`(手写 partial,整体重写)
- 生成侧(不动): `Assets\Scripts\Configs\Language\LanguageConfig.cs` 由 Luban 生成(Plan 1 Task 12 模板:`public static string {key} => Get({index});` + `Get(int)`)

**Interfaces:**
- Consumes: 数组 bin `{varint count}{value*}`;LLP2 `magic+sigId+varint n+{(varint idx,string val)}+varint m+{varint idx}`
- Produces: `static string[] dataArrRef` + `LanguageConfig(ByteBuf)` + `ApplyDelta(ByteBuf)`(LLP2)

- [ ] **Step 1: 重写手写 partial**

```csharp
using Luban;

namespace Table
{
    /// <summary>
    /// 手写 partial:数组格式语言数据({count}{value*},下标=注册表位置)。
    /// 静态访问器与 Get(int) 由生成的 partial 提供(读取 dataArrRef)。
    /// </summary>
    public partial class LanguageConfig
    {
        private static string[] dataArr;

        public LanguageConfig(ByteBuf buf)
        {
            int n = buf.ReadSize();
            dataArr = new string[n];
            for (int i = 0; i < n; i++)
            {
                dataArr[i] = buf.ReadString();
            }
        }

        /// <summary>数组长度(注册表长度,含墓碑槽)。</summary>
        public int Count => dataArr.Length;

        /// <summary>
        /// 应用多语言增量补丁(LLP2,全下标)。
        /// upsert:下标越界则扩容;delete:置空串(槽位保留)。
        /// </summary>
        public void ApplyDelta(ByteBuf buf)
        {
            if (buf.ReadByte() != 'L' || buf.ReadByte() != 'L' || buf.ReadByte() != 'P' || buf.ReadByte() != '2')
                throw new SerializationException("expect LLP2");
            buf.ReadString(); // signatureId(外部已校验,跳过)
            int upsertCount = buf.ReadSize();
            for (int i = 0; i < upsertCount; i++)
            {
                int idx = buf.ReadInt();
                string value = buf.ReadString();
                if ((uint)idx >= (uint)dataArr.Length)
                {
                    var grown = new string[idx + 1];
                    dataArr.CopyTo(grown, 0);
                    dataArr = grown;
                }
                dataArr[idx] = value;
            }
            int deleteCount = buf.ReadSize();
            for (int i = 0; i < deleteCount; i++)
            {
                int idx = buf.ReadInt();
                if ((uint)idx < (uint)dataArr.Length)
                {
                    dataArr[idx] = string.Empty;
                }
            }
        }
    }
}
```

> 注意:生成的 partial 引用 `dataArrRef` 而上面字段名是 `dataArr`——**以 Plan 1 Task 12 模板为准**:模板用 `dataArrRef`,此处字段改为 `internal static string[] dataArrRef;`(去掉 `dataArr` 命名差异),`Count`/构造/ApplyDelta 内部引用同步。两处命名必须逐字一致,合并后 Unity 编译即验证。

同时更新 `Tables` partial(同文件下半部,现状已有):`LoadLanguage`/`LanguageChecksumConfig` 部分不变。

- [ ] **Step 2: 编译验证**

Unity 打开工程编译(或 `dotnet build` 生成的 csproj)。Expected: `LanguageConfig` 合并编译通过;`GetOrDefault`/`this[string]` 调用点此刻**会报编译错**——Task 7 处理前,可先把报错清单记下来(grep 已知 ~13 处,见 Task 7)。

- [ ] **Step 3: Commit(客户端仓)**

```powershell
cd D:\work\slg\client\game
git add Assets/Scripts/Configs/LanguageConfig.cs
git commit -m "feat: LanguageConfig 切换数组格式 + LLP2 ApplyDelta"
```

---

### Task 5: ConfigTableInitializer + ConfigSync 适配

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Scripts\Configs\ConfigTableInitializer.cs:86`(`DataMap.Count` 日志)
- Modify: `D:\work\slg\client\game\Assets\Scripts\Configs\Tables.ConfigSync.cs`(ApplyLanguageDelta 注释与 LLP2 校验,如签名校验已兼容则仅改注释)

**Interfaces:**
- Consumes: Task 4 的 `Count` 属性

- [ ] **Step 1: 日志与注释**

`LoadLanguageAsync` 中 `tables.LanguageConfig.DataMap.Count` → `tables.LanguageConfig.Count`;`VerifyLanguagePatchSignature` 若读 magic(检查其实现——目前只读 sigId 字符串,LLP2 兼容,无需改逻辑,仅更新注释 LLP1→LLP2)。

- [ ] **Step 2: MarchCoreTests 修复**

`Game/Editor/March/MarchCoreTests.cs:21` 注入 `dataMapRef` 的测试桩改为注入 `dataArrRef`(构造 `LanguageConfig` 的小数组或直接反射赋值;测试桩最小改动,按该文件实际断言调整)。

- [ ] **Step 3: 编译 + Commit**

```powershell
git add Assets/Scripts/Configs
git commit -m "feat: 配置初始化/增量同步适配数组语言格式"
```

---

### Task 6: AOT 升级本地化数组化(UpgradeLocalization)

**Files:**
- Modify: `D:\work\slg\client\game\Assets\Scripts\AOT\Runtime\Framework\Upgrade\UpgradeLocalization.cs`(流式读取 `[count][key][val]` → `[count][val]*`)

**Interfaces:**
- Consumes: aot space 数组格式 bin(文件名不变 `languageaotconfig.bytes`)

- [ ] **Step 1: 改流式解析**

打开文件,定位读 bin 的方法(约 344 行注释处"流式读取 languageaotconfig bin([count][key][val]...)"):改为 `[count] + count 个顺序字符串(下标即位置)`;原"key→val 查找"改为按下标读(该类若有 key 查找入口,改为注册表下标——AOT 场景引用的 key 都是 is_code 静态 key,下标已烘焙进 AOT 代码 `LanguageAOTConfig.{key} => Get(n)`,AOT 侧调用点跟随生成代码变化,手写部分只需解析器支持数组)。

- [ ] **Step 2: 验证**

AOT 升级流程冒烟(编辑器模拟:LauncherEditor 触发一次 AOT 本地化加载),确认 `languageaotconfig.bytes` 可解析。

- [ ] **Step 3: Commit**

```powershell
git add Assets/Scripts/AOT
git commit -m "feat: AOT 升级本地化切换数组格式"
```

---

### Task 7: GetOrDefault(string) 调用点迁移

**Files(执行时先重新 grep 确认全集):**

```powershell
git grep -n "LanguageConfig.GetOrDefault\|LanguageConfig\[" -- "*.cs"
```

已知清单(2026-08-21 grep):

| 文件:行 | 现状 | 处置 |
|---|---|---|
| `AOT/Editor/Launcher/LauncherEditor.cs:138` | `GetOrDefault(nameId)` | nameId 来源查明:配置表字段则随字段转 int 改 `Get(int)`;运行拼串则改字面量静态访问器 |
| `Game/.../MediatorAllianceExitWarning.cs:142` | `GetOrDefault(limit.ContentId)` | 同上(ContentId 来自联盟限制配置) |
| `Game/.../MediatorCommonItemUse.Help.cs:137` | `GetOrDefault("hospital_btn_instant")` | 字面量 → `LanguageConfig.hospital_btn_instant` |
| `Game/.../MediatorDialogue.cs:255,260` | `GetOrDefault(data.Name)`/`(data.Text)` | guide 配置数据 → 源表字段迁 text 后 `Get(int)`(本任务先查源;若源表未迁,临时 `Get(LanguageConfig.静态key)` 不可行——记录到 Task 8 迁移清单) |
| `Game/.../MediatorEmergencyCall.cs:38,43` | `GetOrDefault(callerName/desc)` | 同上 |
| `Game/.../MediatorMailMain.cs:183` | `GetOrDefault(langKey)` | 查 langKey 构造:字面量拼接 → 静态访问器;数据来源 → int |
| `Game/.../MediatorMailMain.Event.cs:55` | `GetOrDefault("mail_delete_batch_confirm")` | 静态访问器 |
| `Game/.../MediatorMailDetail.Event.cs:22` | `GetOrDefault("mail_delete_confirm")` | 静态访问器 |
| `Game/.../MediatorMailReport.Event.cs:20` | 同上 | 静态访问器 |
| `Game/.../MediatorMailReportOverview.Event.cs:20` | 同上 | 静态访问器 |

- [ ] **Step 1: 逐处迁移**

按上表处置;规则:字面量 → 生成的静态访问器(纯名);来源是配置表 text 字段 → `Get(int)`;来源是运行期动态字符串且无表可依 → 与用户确认(极少数,预期 0~2 处)。

- [ ] **Step 2: 全局断言无残留**

```powershell
git grep -n "GetOrDefault" -- "*.cs" | findstr LanguageConfig
```

Expected: 空(删除 `GetOrDefault(string)` 方法本身在 Task 4 已随重写移除,残留引用编译会报——以编译通过为准)。

- [ ] **Step 3: 编译 + Commit**

```powershell
git add -A Assets/Scripts
git commit -m "refactor: 移除 LanguageConfig 字符串 key 访问,全部走静态访问器/int 下标"
```

---

### Task 8: 首批表头迁移(Item.csv)+ 全量基准 + 验证清单

**Files:**
- Modify: `D:\work\slg\common\config\Tables\01_全局与通用\02_道具\Item.csv`(##type 行 2 个字段)

**Interfaces:**
- Consumes: text 类型(Plan 1 Task 4 转换层)
- Produces: 首个 text 字段表;spec §9 验证证据

- [ ] **Step 1: 表头迁移**

`Item.csv` 第 2 行(`##type`):

```
改前: ##type,int,#备注,string,string&group=c,InBagType,...
改后: ##type,int,#备注,text,text&group=c,InBagType,...
```

(仅 `nameId`/`descriptionId` 两列 string→text;其余列不动。)

- [ ] **Step 2: 全量基准 + 断言**

```powershell
# 删除旧 sidecar 强制全新分配(首次迁移)
Remove-Item Output\baseline\l10n.json, Output\baseline\l10n.aot.json -ErrorAction SilentlyContinue
./1基准配置导出.bat
```

断言:
1. `Output\Client\configs\tbitem.bytes` 中 nameId 为 varint 下标(用临时 ByteBuf 脚本或 Unity 内调试打印 `Tables.TbItem.Get(1).NameId`,期望 `0`——`item_rss_Diamond` 排序后的注册表下标);
2. 生成的 `Item.cs` 字段 `public int NameId;` + `ReadInt()`;
3. 客户端编译通过,道具名显示正常(Play 冒烟:背包/道具详情);

- [ ] **Step 3: spec §9 验证清单(配置仓范围)**

逐项执行并记录(1~8):幂等重跑、追加稳定(增量两次)、墓碑/复活、未知 key 报错文案、LLP2 应用(Get(n) 与重导一致)、checksum 对齐(checksumconfig.bytes 内语言行 MD5 = 文件实际 MD5)、~640 静态调用抽查 5 处、server run 独立跑通。

- [ ] **Step 4: Commit**

```powershell
cd D:\work\slg\common\config
git add "Tables/01_全局与通用/02_道具/Item.csv"
git commit -m "feat: Item 表 nameId/descriptionId 迁移 text 类型(首批 int 下标)"
```

---

### Task 9: 服务端格式文档

**Files:**
- Create: `D:\work\slg\common\config\docs\l10n-int-index-format.md`(给服务端团队)

- [ ] **Step 1: 写文档**

内容(spec §7):
1. 业务表 json:text 字段变 int(下标空间 = main 注册表);
2. `configs/client/language/{lang}/languageconfig.bytes` 数组格式 `{varint count}{value*}`;
3. LLP2 patch 布局(贴 Plan 1 Task 9 的格式定义);
4. 注册表权威 = `Output/baseline/l10n.json` 的 `KeyEntries`(位置=下标,Deleted=墓碑);
5. 下标稳定规则(追加/墓碑/重置=删 sidecar 重基准)。

- [ ] **Step 2: Commit**

```powershell
git add docs/l10n-int-index-format.md
git commit -m "docs: 多语言 int 下标格式说明(服务端适配用)"
```

---

## Self-Review 结论

- **Spec 覆盖**:§5.1(Task 1)、§5.2(Task 2/3)、§5.3(Task 8)、§6.1(Task 4)、§6.2(Task 7)、§6.3(Task 5)、AOT(Task 6,spec §6.1 末句)、§7(Task 9)。spec §2 的"server run 独立跑通"在 Task 8 Step 3 验证。
- **占位符**:Task 7 表格中"同上/查明来源"类处置均给出了判定规则(字面量→静态访问器;表字段→int;动态→确认),无悬空 TBD;Task 1 Step 2 的命名冲突处理给了判定原则。
- **跨计划一致性**:`dataArrRef` 命名(Task 4)与 Plan 1 Task 12 模板逐字对齐;LLP2 布局与 Plan 1 Task 9 测试逐字段对齐;bat 的 space 参数与 Global Constraints 的事实表逐项对齐。
