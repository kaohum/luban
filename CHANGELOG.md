## 变更日志

### 2026-02-03

- **扩展基础类型，支持时间相关类型**
  - 新增 5 种时间相关的基础类型：`day`、`hour`、`minute`、`second`、`millisecond`。
  - 新增对应的数据类型类：
    - `TDay` / `DDay`：表示天数，底层为 int 类型。
    - `THour` / `DHour`：表示小时数，底层为 int 类型。
    - `TMinute` / `DMinute`：表示分钟数，底层为 int 类型。
    - `TSecond` / `DSecond`：表示秒数，底层为 int 类型。
    - `TMillisecond` / `DMillisecond`：表示毫秒数，底层为 long 类型。
  - 在 `DefAssembly` 中注册新的时间类型，支持在配置表中直接使用。
  - 在 `DataUtil` 中新增时间类型的解析逻辑，支持多种时间格式输入：
    - 纯数字格式：直接表示对应单位的数值（如 `100` 表示 100 天/小时/分钟/秒/毫秒）。
    - 带单位格式：支持 `d`（天）、`h`（小时）、`m`（分钟）、`s`（秒）、`ms`（毫秒）等单位后缀。
    - 复合格式：支持多个单位组合，如 `1d2h30m`（1天2小时30分钟）。
    - 自动单位转换：输入会自动转换为目标类型的基础单位。
  - 更新所有语言代码生成器，支持新时间类型的序列化和反序列化：
    - C#：支持 Binary、JSON（DotNet/NewtonSoft/Simple）、String 等多种序列化方式。
    - C++：支持底层类型声明和反序列化。
    - Java：支持 Binary 和 JSON 反序列化。
    - Go：支持 Binary 和 JSON 反序列化。
    - Lua：支持类型注释和反序列化方法。
    - TypeScript/JavaScript：支持 Binary 和 JSON 反序列化。
    - Python：支持 JSON 反序列化。
    - Rust：支持 Binary 和 JSON 反序列化。
    - Dart：支持 JSON 反序列化。
    - PHP：支持 JSON 反序列化。
    - GDScript：支持类型声明和反序列化。
  - 更新所有数据加载器，支持从各种数据源（Excel、JSON、XML、YAML、Lua 等）加载时间类型数据。
  - 更新所有数据导出器，支持将时间类型导出为 Binary、JSON、XML、YAML、Protobuf 等格式。
  - 时间类型可用作表的主键和索引字段。
  - 详细使用说明参见 `docs/时间类型使用文档.md` 文档。

### 2026-02-02

- **支持 CSV 中间格式的导出**
  - 新增 CSV 源文件导出功能，可将原始数据源（如 Excel、CSV 等）转换为标准化的 CSV 格式中间产物。
  - 在 `BuiltinOptionNames` 中新增 `CsvSourceOutputDir` 配置项，用于指定 CSV 源文件输出目录。
  - 新增 `CsvSourceExporter` 类，实现 CSV 源文件的收集和导出逻辑：
    - 使用单例模式管理全局导出器实例。
    - 通过 `ConcurrentBag` 收集来自多个数据源的 CSV 数据。
    - 支持按源文件分组导出，自动处理字段转义和格式化。
  - 在 `ExcelRowColumnDataSource.Load()` 方法中捕获原始数据，包括 Sheet 名称、字段名、类型、描述等元信息。
  - 在 `DefaultPipeline.LoadDatas()` 完成后自动调用 CSV 导出。
  - 导出的 CSV 文件格式包含：
    - Sheet 名称标记（`## Sheet: SheetName`）
    - 原始数据行（保留所有字段和元信息）
    - 自动转义特殊字符（逗号、引号、换行符等）
  - 便于在版本控制系统（如 Git）中进行数据对比和合并。
  - 详细使用说明参见 `CSV_SOURCE_USAGE.md` 文档。

### 2026-01-30

- **修复 Tag 导出时 Target Group 不生效的问题**
  - 修复在使用 Tag 过滤导出时，Target 的 Group 配置不生效的问题。
  - 在 `GenerationContext` 中增强了 Tag 与 Group 的关联处理逻辑。

- **多语言导出支持值类型（int 等）作为 Key**
  - 扩展本地化多语言导出功能，支持使用值类型（如 int、long 等）作为本地化键。
  - 在 `DType` 中新增值类型判断方法，用于区分引用类型和值类型。
  - 优化 `L10NBinarySplitDataExporter` 的键类型处理逻辑，支持更灵活的键类型配置。
  - 更新 `CsharpL10NLanguageCodeTarget`，生成的代码支持值类型键的本地化查询。
  - 在 `GenerationContext` 和 `L10NKeyInfo` 中优化本地化键信息的处理。

- **Dll 输出目录调整**
  - 统一调整多个项目的 Dll 输出目录配置，优化构建输出结构。
  - 涉及项目：
    - Luban.Bson
    - Luban.Cpp
    - Luban.Dart
    - Luban.Golang
    - Luban.Lua
    - Luban.Protobuf
    - Luban.Schema.Builtin

- **支持导出 CSV 格式比对文件**
  - 新增 CSV 数据导出器（`CsvDataTarget`），支持将 Luban 配置表导出为 CSV 格式。
  - 新增 `CsvDataVisitor`，实现配置数据到 CSV 格式的转换逻辑。
  - 支持的配置选项：
    - `delimiter`：字段分隔符，默认为逗号 `,`
    - `header`：是否输出表头，默认为 `true`
    - `file_encoding`：文件编码，默认为 UTF-8
    - `output_data_extension`：输出文件扩展名，默认为 `.csv`
  - 复杂类型格式化规则：
    - Bean（结构体）：`{field1:value1;field2:value2}`
    - Array/List（数组/列表）：`[value1;value2;value3]`
    - Map（字典）：`{key1:value1;key2:value2}`
  - 自动处理特殊字符转义，确保 CSV 格式正确性。
  - 方便与 Excel、数据库等工具进行数据交换和对比。
  - 详细使用说明参见 `src/Luban.DataTarget.Builtin/Csv/README.md` 文档。

### 2026-01-21

- **支持多值索引自动检测**
  - 新增对 LIST 表的多值索引自动检测功能：当索引字段在数据中有重复值时，自动生成返回列表的索引方法。
  - 在 `IndexInfo` 中新增 `IsMultiValue` 属性，用于标记索引是否为多值索引（一个key对应多个value）。
  - 在 `TableDataInfo.BuildIndexs` 方法中自动检测索引值重复情况：
    - 如果索引字段值在数据中唯一，生成 `GetByXxx()` 方法返回单个对象。
    - 如果索引字段值有重复，生成 `GetByXxxList()` 方法返回 `IReadOnlyList<TValue>`。
  - 示例场景：建筑配置表有 `id`(int) 和 `type`(枚举) 字段，可通过 `id` 索引获取单个建筑对象，也可通过 `type` 索引获取该类型的所有建筑对象列表。
  - 多值索引使用 `Dictionary<TKey, List<TValue>>` 存储，避免频繁的数组转换，提升 Append 操作性能。
  - 向后兼容：如果 LIST 表的所有索引值都唯一，生成的代码与之前完全相同。
  - MAP 表保持现有行为不变，仅 LIST 表支持此特性。

### 2026-01-16

- **支持多维数组定义方式**
  - 新增对 `int[,]`、`list<int>[,]` 等多维数组类型的定义支持。
  - 优化类型解析逻辑，允许在配置表中直接使用多维数组语法。

### 2025-12-18

- **支持索引键的混用模式**
  - 新增混合索引键功能：支持在同一个表中定义多种索引组合，例如 `a,a+b,c`，会自动生成 `getBy_a`、`getBy_ab`、`getBy_c` 三个获取方法。
  - 便于在不同场景下以不同的键组合快速查询数据。

- **增加归一化的 ID 字段名称**
  - 在 `GenerationContext` 中增加归一化的 ID 字段名称处理。
  - 统一本地化键信息 (`L10NKeyInfo`) 的字段名称规范。

- **本地化语言导出优化**
  - 改进本地化语言的代码导出方式，提升代码生成质量。
  - 优化本地化语言导出工具的处理逻辑。

- **修复与优化**
  - 修复日志打印相关问题。
  - 修复数据排序相关问题。

### 2025-12-09

- **导表工具固化排序**
  - 对导表工具进行固化排序处理，保证每次导表生成的数据顺序一致。
  - 在 `GenerationContext` 中对表和数据进行排序，确保导出结果的稳定性和可重现性。

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
