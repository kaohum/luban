# CSV源文件导出功能使用说明

## 功能概述

CSV源文件导出功能可以将原始数据源（如Excel、CSV等）转换为标准化的CSV格式中间产物，方便在版本控制系统（如Git）中进行数据对比和合并。

## 主要特性

1. **自动转换**：在数据加载过程中自动将Excel等格式转换为CSV
2. **保留结构**：保留原始数据的字段名、类型、描述等元信息
3. **便于对比**：CSV格式便于在Git等工具中进行diff和merge
4. **无侵入性**：不影响原有的导表流程，可选启用

## 使用方法

### 1. 配置方式

在Luban配置文件中添加CSV源文件输出目录配置：

```json
{
  "targets": [
    {
      "name": "your_target",
      "options": {
        "csvSourceOutputDir": "./csv_source"
      }
    }
  ]
}
```

或者通过命令行参数传递：

```bash
dotnet Luban.dll -t your_target -c your_code_target -d your_data_target \
  -x csvSourceOutputDir=./csv_source \
  --conf luban.conf
```

### 2. 配置参数说明

- **csvSourceOutputDir**: CSV源文件输出目录
  - 类型：字符串
  - 默认值：空（不导出）
  - 说明：指定CSV源文件的输出目录，如果不设置或为空，则不导出CSV源文件

### 3. 输出文件格式

导出的CSV文件格式如下：

```csv
## Sheet: SheetName
field1,field2,field3
##type,int,string,float
##desc,字段1描述,字段2描述,字段3描述
1,value1,1.5
2,value2,2.5
```

**格式说明：**
- 第一行：Sheet名称（如果有多个Sheet）
- 第二行：字段名
- 第三行：类型定义（以##type开头）
- 第四行：字段描述（以##desc开头）
- 后续行：数据行

### 4. 完整示例

假设你有以下项目结构：

```
project/
├── luban.conf
├── Defines/
│   └── tables.xlsx
└── Datas/
    └── item.xlsx
```

**配置文件 (luban.conf):**

```json
{
  "schemaFiles": [
    {
      "fileName": "Defines/tables.xlsx",
      "type": "excel"
    }
  ],
  "targets": [
    {
      "name": "server",
      "options": {
        "inputDataDir": "./Datas",
        "outputCodeDir": "./Gen/Code",
        "outputDataDir": "./Gen/Data",
        "csvSourceOutputDir": "./Gen/CsvSource"
      }
    }
  ]
}
```

**运行命令：**

```bash
dotnet Luban.dll -t server -c cs-bin -d bin \
  --conf luban.conf
```

**输出结果：**

```
project/
├── Gen/
│   ├── Code/          # 生成的代码
│   ├── Data/          # 生成的数据
│   └── CsvSource/     # CSV源文件（新增）
│       └── item.csv
```

### 5. Git集成建议

将CSV源文件纳入版本控制：

```bash
# 添加到Git
git add Gen/CsvSource/

# 提交
git commit -m "Update data tables"
```

**优势：**
- 可以清晰看到数据的变更历史
- 方便进行代码审查（Code Review）
- 合并冲突时更容易解决
- 可以使用Git的diff工具对比数据变化

### 6. 注意事项

1. **性能影响**：导出CSV源文件会略微增加导表时间，但影响很小
2. **磁盘空间**：CSV文件会占用额外的磁盘空间
3. **编码格式**：导出的CSV文件使用UTF-8编码
4. **字段转义**：包含逗号、引号、换行符的字段会自动用引号包裹并转义

### 7. 高级用法

#### 7.1 仅导出CSV源文件

如果只想导出CSV源文件而不生成代码和数据：

```bash
dotnet Luban.dll -t server \
  -x csvSourceOutputDir=./csv_source \
  -f \
  --conf luban.conf
```

参数说明：
- `-f` 或 `--forceLoadTableDatas`：强制加载表数据（即使没有指定dataTarget）

#### 7.2 清理输出目录

建议在导出前清理输出目录，避免残留旧文件：

```bash
# Windows
rmdir /s /q Gen\CsvSource
dotnet Luban.dll -t server -c cs-bin -d bin --conf luban.conf

# Linux/Mac
rm -rf Gen/CsvSource
dotnet Luban.dll -t server -c cs-bin -d bin --conf luban.conf
```

## 实现原理

1. **数据捕获**：在`ExcelRowColumnDataSource.Load()`方法中捕获原始数据
2. **数据收集**：将捕获的数据存储到`CsvSourceExporter`的单例中
3. **批量导出**：在`DefaultPipeline.LoadDatas()`完成后统一导出所有CSV文件
4. **格式转换**：将原始数据转换为标准CSV格式，包含字段名、类型、描述等元信息

## 技术细节

### 修改的文件

1. **Luban.Core/BuiltinOptionNames.cs**
   - 添加了`CsvSourceOutputDir`配置项

2. **Luban.Core/CsvSource/CsvSourceExporter.cs**（新增）
   - CSV源文件导出器核心实现

3. **Luban.Core/Pipeline/DefaultPipeline.cs**
   - 在`LoadDatas()`后调用CSV导出

4. **Luban.DataLoader.Builtin/Excel/ExcelRowColumnDataSource.cs**
   - 在数据加载时捕获原始数据

### 扩展性

如果需要支持其他数据源格式（如JSON、XML等），可以在对应的DataLoader中添加类似的数据捕获逻辑。

## 常见问题

**Q: CSV源文件和最终输出的数据文件有什么区别？**

A: CSV源文件是原始数据的标准化表示，保留了所有字段和元信息；而最终输出的数据文件是经过处理、验证、转换后的二进制或JSON格式，用于游戏运行时加载。

**Q: 是否必须启用此功能？**

A: 不是必须的。如果不配置`csvSourceOutputDir`，导表工具会正常工作，只是不会生成CSV源文件。

**Q: CSV源文件会影响导表性能吗？**

A: 影响很小。导出CSV是在数据加载完成后进行的，不会阻塞主要的导表流程。

**Q: 可以只导出部分表的CSV源文件吗？**

A: 当前版本会导出所有加载的表。如果需要选择性导出，可以通过`-o`参数指定要导出的表。

## 反馈与支持

如有问题或建议，请联系开发团队。

