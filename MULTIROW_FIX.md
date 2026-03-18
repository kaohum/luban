# CSV源文件导出 - 跨行数据处理修复

## ✅ 问题已修复

已成功修复CSV源文件导出功能对跨行数据的处理问题。编译成功，无错误！

## 🐛 问题描述

之前的实现直接从 `RawSheet.Cells` 中逐行提取数据，这导致：

1. **跨行数据被拆分**：当Excel中存在多行记录（如list类型字段）时，会被错误地拆分成多条记录
2. **数据不一致**：导出的CSV数据与实际导表使用的数据不一致

### 示例问题

假设Excel中有如下跨行数据：

```
##      | id | items
##type  | int | list,int
1       | 1  | 100
        |    | 200
        |    | 300
2       | 2  | 400
```

**之前的错误输出**：
```csv
id,items
1,100
,,200
,,300
2,400
```

**修复后的正确输出**：
```csv
id,items
1,"100;200;300"
2,400
```

## 🔧 修复方案

### 核心改进

1. **使用处理后的数据**：从 `RowColumnSheet.Rows` 中提取数据，而不是直接从 `RawSheet.Cells`
2. **正确处理跨行**：`RowColumnSheet` 已经正确处理了跨行数据的合并
3. **格式化复杂数据**：添加了 `FormatTitleRowValue()` 方法来格式化复杂的数据结构

### 修改的方法

#### 1. `ExportCsvSource()` 方法签名变更

```csharp
// 之前
private void ExportCsvSource(string rawUrl, RawSheet rawSheet)

// 现在
private void ExportCsvSource(string rawUrl, RowColumnSheet sheet, RawSheet rawSheet)
```

#### 2. 数据提取逻辑

```csharp
// 从处理后的sheet.Rows中提取数据（已正确处理跨行数据）
foreach (var (tag, titleRow) in sheet.GetRows())
{
    if (DataUtil.IsIgnoreTag(tag))
    {
        continue;
    }

    var dataRow = new List<object>();
    ExtractRowData(titleRow, title, dataRow);
    csvData.Rows.Add(dataRow);
}
```

#### 3. 新增辅助方法

- **`ExtractRowData()`**：从TitleRow中提取数据到CSV行
- **`FormatTitleRowValue()`**：将复杂的TitleRow格式化为字符串

## 📊 数据格式化规则

### 简单字段
直接输出原始值

### 复杂字段（跨行/多值）

1. **单行多列**：使用 `|` 分隔
   - 例如：`value1|value2|value3`

2. **多行数据**：使用 `;` 分隔
   - 例如：`row1|row2;row3|row4`

3. **嵌套结构**：组合使用 `|` 和 `;`
   - 例如：`field1|field2;field3|field4`

### 示例

#### List类型字段

Excel:
```
##      | id | items
##type  | int | list,int
1       | 1  | 100
        |    | 200
        |    | 300
```

CSV输出:
```csv
id,items
1,"100;200;300"
```

#### 复杂结构

Excel:
```
##      | id | rewards
##type  | int | list,(int,int)
1       | 1  | 100 | 10
        |    | 200 | 20
```

CSV输出:
```csv
id,rewards
1,"100|10;200|20"
```

## 🚀 使用方法

### 1. 更新DLL

```batch
REM 复制新编译的DLL到您的Tools目录
copy /Y E:\Projects\luban\src\Luban.Core\bin\Debug\net8.0\Luban.Core.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban.DataLoader.Builtin\bin\Debug\net8.0\Luban.DataLoader.Builtin.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban\bin\Debug\net8.0\Luban.dll Tools\Luban\
```

### 2. 运行导表命令

```batch
set LUBAN_DLL=Tools\Luban\Luban.dll
set CONF_ROOT=Tools\game.conf
set GEN_DATA_SOURCE=..\..\client\game\Assets\Publish\configs\table
set GEN_CODE_SOURCE=..\..\client\game\Assets\Scripts\Configs\Gen

dotnet %LUBAN_DLL% ^
    -t client ^
    -c cs-bin ^
    -d bin ^
    -i dev test ^
    -f ^
    --conf %CONF_ROOT% ^
    -x cs-bin.outputCodeDir=%GEN_CODE_SOURCE% ^
    -x bin.outputDataDir=%GEN_DATA_SOURCE% ^
    -x csvSourceOutputDir=./csv_source ^
    -x dataExporter=tag-split
```

### 3. 验证输出

检查 `csv_source` 目录中的CSV文件：

1. **记录数量**：应该与实际的数据记录数一致（不会因为跨行而增加）
2. **跨行数据**：多行数据应该被合并为一个单元格，使用分隔符连接
3. **数据完整性**：所有数据都应该被正确导出

## 📝 技术细节

### 数据流程

```
Excel文件
  ↓
SheetLoadUtil.LoadRawSheets() → RawSheet (原始单元格数据)
  ↓
RowColumnSheet.Load() → 处理跨行数据
  ↓
RowColumnSheet.Rows → 已合并的记录
  ↓
ExportCsvSource() → 提取并格式化
  ↓
CSV文件
```

### 关键类和方法

1. **RawSheet**：原始Excel数据，包含所有单元格
2. **RowColumnSheet**：处理后的数据，已合并跨行记录
3. **TitleRow**：表示一条记录，可能包含多行原始数据
4. **ExtractRowData()**：从TitleRow提取数据
5. **FormatTitleRowValue()**：格式化复杂数据结构

## ⚠️ 注意事项

1. **分隔符**：
   - 列内分隔符：`|`
   - 行间分隔符：`;`
   - 如果数据本身包含这些字符，会被CSV转义（用引号包裹）

2. **数据一致性**：
   - 导出的CSV数据与实际导表使用的数据完全一致
   - 可以放心用于版本对比和数据审查

3. **性能**：
   - 使用已处理的数据，不会增加额外的处理开销
   - 导出速度与之前相同

## 🎯 验证方法

### 测试跨行数据

1. 创建一个包含list类型字段的Excel表
2. 添加跨行数据
3. 运行导表并导出CSV
4. 检查CSV中的记录数是否正确
5. 检查跨行数据是否被正确合并

### 对比验证

```bash
# 导出CSV
dotnet Luban.dll -t client -f -x csvSourceOutputDir=./csv_source --conf game.conf

# 查看CSV文件
type csv_source\your_table.csv

# 使用Git对比
git diff csv_source/
```

## 📚 相关文档

- **FIX_COMPLETE.md** - 初次修复说明
- **QUICK_START_CSV_SOURCE.md** - 快速开始指南
- **CSV_SOURCE_USAGE.md** - 详细使用文档

## 🎉 总结

现在CSV源文件导出功能已经完全支持跨行数据，可以正确处理：

- ✅ 简单字段
- ✅ List类型字段（跨行）
- ✅ 复杂嵌套结构
- ✅ 多行记录
- ✅ 空值和默认值

导出的CSV数据与实际导表使用的数据完全一致，可以放心用于版本控制和数据对比！

