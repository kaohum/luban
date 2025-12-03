## 变更日志

### 2025-12-03

- **新增 Tag 默认值与过滤规则**
  - 在 `Record` 中增加默认 Tag，若一条记录未显式配置 Tag，则自动添加 `base` 作为默认 Tag。
  - 调整 `Record.IsNotFiltered` 逻辑：  
    - 仅当记录包含除 `base` 以外的业务 Tag 时，才参与 `-i/--includeTag` 与 `-e/--excludeTag` 的过滤判断。  
    - 仅带 `base` 的记录在未被显式排除的情况下视为“无业务 Tag”，始终参与导出。
  - 主键校验逻辑改为：**仅在存在相同主键且 Tag 有交集时才视为冲突**，不同 Tag 的记录允许使用相同主键。

- **命令行 Tag 参数规范化**
  - 在 `Program` 中对 `--includeTag/-i` 与 `--excludeTag/-e` 进行统一归一化：`trim + ToLower + 去重`。  
  - 与 `DataUtil.ParseTags` 保持一致，避免大小写不一致导致匹配失败。

- **按 Tag 拆分数据导出**
  - 新增数据导出器 `tag-split`（`TagSplitDataExporter`）：  
    - 在 conf 中通过 `dataExporter = "tag-split"` 启用。  
    - 支持命令行 `-i dev test` 时，将数据导出到 `dev/xxx`、`test/xxx`、`base/xxx` 等子目录。  
    - 未使用 `-i` 时行为与原有默认导出器一致，不改变目录结构。  
    - 仅影响**数据文件导出目录结构**，**代码生成逻辑保持不变**。

- **GenerationContext 中的 Tag 相关上下文**
  - 新增 `GenerationContext.AllTags`：  
    - 表示当前运行环境中所有有效的 Tag，通常为命令行 `-i` 传入的 Tag 与默认 `base` 的并集。  
    - 供 Scriban 模板在生成加载代码时循环使用。
  - 新增 `GenerationContext.TablesByTag`：  
    - 类型为 `IReadOnlyDictionary<string, List<DefTable>>`。  
    - key 为 Tag（小写），value 为在该 Tag 下**实际有数据导出的表列表**。  
    - 在 `AddDataTable` 期间基于真实数据填充，仅统计 `AllTags` 中的 Tag，且只记录 `FinalRecords` 非空的表。  
    - 方便模板内实现“按 Tag 加载当前环境下所有有效表”的逻辑。

- **默认流水线 DefaultPipeline 行为调整**
  - 当命令行指定 `--forceLoadTableDatas/-f` 时：  
    - 在处理 Code Target 前先调用 `LoadDatas`，从而允许代码模板安全访问基于数据的上下文（如 `TablesByTag`）。  
  - 当存在 Data Target 时、且未显式指定 `-f`：  
    - 保持原有行为：在处理 Data Target 前调用 `LoadDatas`。  
  - 此调整确保：  
    - 使用 `-f` 时，代码生成与数据生成均可依赖完整数据上下文。  
    - 不使用 `-f` 且仅生成代码时，不会引入额外的数据加载开销。

- **本地化多语言按字段拆分导出（JSON）**
  - 新增数据导出器 `l10n-json-split`（`L10NJsonSplitDataExporter`）：  
    - 按本地化语言列将表数据拆分为多份 JSON 文件，输出路径为 `{lang}/{TableName}.json`（例如 `zh_CN/Language.json`）。  
    - 通过 `-x l10n.languages=zh_CN,en_US` 等配置导出语言列，通过 `-x l10n.textFile.keyFieldName=Id` 指定 key 字段。  
    - 通过 `-x l10n.keepMergedJson=true|false` 控制是否保留原有合并语言的 JSON 文件。

- **本地化多语言按字段拆分导出（二进制）**
  - 新增数据导出器 `l10n-bin-split`（`L10NBinarySplitDataExporter`）：  
    - 按本地化语言列将表数据拆分为多份二进制文件，输出路径为 `{lang}/{TableName}.bytes`。  
    - 二进制内容为简单的 `Dictionary<string,string>`，使用 `ByteBuf` 依次写入 `count`、以及每条记录的 `key`、`value` 字符串，便于运行时按语言快速加载和切换。  
    - 同样通过 `-x l10n.languages`、`-x l10n.textFile.keyFieldName`、`-x l10n.keepMergedBin` 进行配置。


