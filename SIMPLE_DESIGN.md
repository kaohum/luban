# CSV源文件导出 - 简化版实现

## ✅ 重新设计完成

根据您的反馈，我重新设计了CSV源文件导出功能，采用**极简方案**：

**核心思想：原样转换Excel到CSV，就像"另存为CSV"一样简单。**

## 🎯 设计理念

### 之前的问题
- ❌ 过度设计，试图处理跨行、跨列、复杂结构
- ❌ 使用处理后的数据，导致与原始Excel不一致
- ❌ 代码复杂，难以维护

### 现在的方案
- ✅ **原样转换**：直接将Excel的每个单元格转换为CSV
- ✅ **保持原貌**：包括所有的##标记行、空行、跨行数据
- ✅ **极简代码**：只需10行核心代码

## 📝 核心代码

```csharp
private void ExportCsvSource(string rawUrl, RawSheet rawSheet)
{
    var csvData = new CsvSourceData
    {
        SourceFile = rawUrl,
        SheetName = rawSheet.SheetName
    };

    // 直接原样转换所有行，不做任何处理
    foreach (var row in rawSheet.Cells)
    {
        var dataRow = new List<object>();
        foreach (var cell in row)
        {
            dataRow.Add(cell.Value);
        }
        csvData.Rows.Add(dataRow);
    }

    CsvSourceExporter.Instance.RecordCsvSourceData(csvData);
}
```

就这么简单！

## 📊 转换效果

### Excel原始格式

```
| ##     | id  | name | items |
| ##type | int | string | list,int |
| ##desc | ID  | 名称 | 物品列表 |
|        | 1   | 剑   | 100   |
|        |     |      | 200   |
|        |     |      | 300   |
|        | 2   | 盾   | 400   |
```

### CSV输出（完全一致）

```csv
##,id,name,items
##type,int,string,"list,int"
##desc,ID,名称,物品列表
,1,剑,100
,,,200
,,,300
,2,盾,400
```

**完全保留原始格式**，包括：
- ✅ 所有##标记行
- ✅ 空单元格
- ✅ 跨行数据的原始结构
- ✅ 跨列数据的原始结构

## 🚀 使用方法

### 1. 更新DLL

```batch
copy /Y E:\Projects\luban\src\Luban.Core\bin\Debug\net8.0\Luban.Core.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban.DataLoader.Builtin\bin\Debug\net8.0\Luban.DataLoader.Builtin.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban\bin\Debug\net8.0\Luban.dll Tools\Luban\
```

### 2. 运行导表

```batch
set LUBAN_DLL=Tools\Luban\Luban.dll
set CONF_ROOT=Tools\game.conf

dotnet %LUBAN_DLL% ^
    -t client ^
    -c cs-bin ^
    -d bin ^
    -i dev test ^
    -f ^
    --conf %CONF_ROOT% ^
    -x csvSourceOutputDir=./csv_source ^
    -x dataExporter=tag-split
```

### 3. 查看结果

```batch
dir csv_source
type csv_source\your_table.csv
```

## 💡 优势

### 1. 完全一致
CSV文件与Excel文件**完全一致**，可以直接用于：
- Git diff对比
- 数据审查
- 版本控制
- 分支合并

### 2. 极简实现
- 核心代码只有10行
- 无需处理复杂的数据结构
- 易于理解和维护

### 3. 通用性强
适用于所有Excel格式：
- 简单表格
- 跨行数据
- 跨列数据
- 复杂嵌套结构
- 任何自定义格式

## 🔍 Git对比示例

### 修改前
```csv
,1,剑,100
,,,200
```

### 修改后
```csv
,1,剑,100
,,,200
,,,300
```

### Git diff
```diff
 ,1,剑,100
 ,,,200
+,,,300
```

清晰地看到添加了一行数据！

## 📋 技术细节

### 数据流程

```
Excel文件
  ↓
ExcelDataReader 读取
  ↓
RawSheet.Cells (原始单元格数组)
  ↓
逐行逐列转换
  ↓
CSV文件（完全一致）
```

### 关键点

1. **不做任何处理**：直接转换原始单元格值
2. **保留空单元格**：空值也会输出为空CSV字段
3. **自动转义**：CSV导出器会自动处理逗号、引号等特殊字符
4. **UTF-8编码**：确保中文等字符正确显示

## ⚠️ 注意事项

1. **CSV格式**：
   - 包含逗号的值会自动用引号包裹
   - 包含引号的值会自动转义为双引号
   - 包含换行的值会用引号包裹

2. **文件大小**：
   - CSV文件通常比Excel小
   - 适合Git版本控制

3. **编辑建议**：
   - 不建议直接编辑CSV文件
   - 应该编辑Excel，然后重新导出

## 🎉 总结

新的实现方案：
- ✅ **极简**：核心代码只有10行
- ✅ **准确**：完全保留Excel原始格式
- ✅ **通用**：支持所有Excel格式
- ✅ **高效**：无额外处理开销
- ✅ **易维护**：代码简单清晰

这才是正确的设计！感谢您的指正！🙏

## 📚 相关文件

修改的文件：
1. `Luban.Core/CsvSource/CsvSourceExporter.cs` - 简化导出逻辑
2. `Luban.DataLoader.Builtin/Excel/ExcelRowColumnDataSource.cs` - 简化数据提取

删除的复杂逻辑：
- ❌ ExtractRowData() 方法
- ❌ FormatTitleRowValue() 方法
- ❌ Title处理逻辑
- ❌ 字段名提取逻辑
- ❌ 类型和描述提取逻辑

现在整个实现非常简洁明了！

