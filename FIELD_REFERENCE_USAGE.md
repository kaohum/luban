# 字段引用追踪功能使用说明

## 功能概述

这个功能可以帮助你追踪配置表中某个字段的所有引用来源。当你指定一个表的字段后，系统会遍历所有配置表，找出所有使用了该字段的表和字段，并生成报告或代码。

## 使用场景

- 查找哪些表引用了某个配置表的ID字段
- 分析配置表之间的依赖关系
- 重构配置表时评估影响范围
- 生成字段引用查询的工具代码

## 使用方式

### 方式一：生成文本报告（DataTarget）

使用 `field-reference` DataTarget 生成文本格式的引用报告：

```bash
# 基本用法
dotnet Luban.dll \
  --conf luban.conf \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Id

# 自定义输出文件名
dotnet Luban.dll \
  --conf luban.conf \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Id \
  -x field-reference.outputFile=item_references.txt
```

**参数说明：**
- `targetTable`: 目标表名（必填）
- `targetField`: 目标字段名（必填）
- `outputFile`: 输出文件名（可选，默认为 `field_reference_{表名}_{字段名}.txt`）

**输出示例：**
```
# 字段引用分析报告
# 目标表: TbItem
# 目标字段: Id
# 生成时间: 2025-02-05 10:30:00

# 统计信息:
# - 引用该字段的表数量: 3
# - 引用该字段的字段数量: 5

# 引用详情:
# 格式: 表名 | 字段名 | 字段类型 | 字段路径
# --------------------------------------------------------------------------------
TbQuest | RewardItemId | int | RewardItemId
TbQuest | RequiredItems | list | RequiredItems
TbShop | ItemId | int | ItemId
TbPlayer | InitialItems | array | InitialItems
TbBag | DefaultItemId | int | DefaultItemId
```

### 方式二：生成C#代码（CodeTarget）

使用 `cs-field-reference` CodeTarget 生成C#代码，可以在运行时查询引用信息：

```bash
# 基本用法
dotnet Luban.dll \
  --conf luban.conf \
  --codeTarget cs-field-reference \
  -x cs-field-reference.targetTable=TbItem \
  -x cs-field-reference.targetField=Id
```

**参数说明：**
- `targetTable`: 目标表名（必填）
- `targetField`: 目标字段名（必填）

**生成的代码示例：**
```csharp
using System;
using System.Collections.Generic;

namespace YourNamespace
{
    /// <summary>
    /// 字段引用查询类
    /// 目标表: TbItem
    /// 目标字段: Id
    /// </summary>
    public static class FieldReference_TbItem_Id
    {
        public class ReferenceInfo
        {
            public string TableName { get; set; }
            public string FieldName { get; set; }
            public string FieldType { get; set; }
            public string FieldPath { get; set; }
        }
        
        public static class Statistics
        {
            public const int TableCount = 3;
            public const int FieldCount = 5;
        }
        
        // 获取所有引用信息
        public static IReadOnlyList<ReferenceInfo> GetAllReferences() { ... }
        
        // 获取指定表的引用信息
        public static IEnumerable<ReferenceInfo> GetReferencesByTable(string tableName) { ... }
        
        // 检查指定表是否引用了该字段
        public static bool HasReference(string tableName) { ... }
        
        // 获取所有引用该字段的表名
        public static IEnumerable<string> GetReferencingTables() { ... }
    }
}
```

**使用生成的代码：**
```csharp
// 获取所有引用
var allReferences = FieldReference_TbItem_Id.GetAllReferences();
foreach (var reference in allReferences)
{
    Console.WriteLine($"{reference.TableName}.{reference.FieldName}");
}

// 检查特定表是否引用
if (FieldReference_TbItem_Id.HasReference("TbQuest"))
{
    Console.WriteLine("TbQuest 表引用了 TbItem.Id");
}

// 获取统计信息
Console.WriteLine($"共有 {FieldReference_TbItem_Id.Statistics.TableCount} 个表引用了该字段");
```

## 工作原理

1. **字段标记**：系统通过 `ref` 标签识别字段引用关系
   ```xml
   <!-- 在配置定义中使用 ref 标签 -->
   <field name="ItemId" type="int" ref="TbItem"/>
   ```

2. **遍历分析**：系统遍历所有配置表的所有字段，检查是否有 `ref` 标签指向目标表

3. **收集信息**：记录所有引用了目标表字段的表名、字段名、字段类型和字段路径

4. **生成输出**：根据选择的目标类型（DataTarget 或 CodeTarget）生成相应的输出

## 注意事项

1. **必须使用 ref 标签**：只有标记了 `ref` 标签的字段才会被识别为引用关系
2. **需要加载数据**：系统需要加载配置数据才能进行分析
3. **性能考虑**：对于大型配置表，分析过程可能需要一些时间
4. **嵌套字段支持**：支持在嵌套结构（如 Bean、List、Array）中查找引用

## 扩展建议

如果你需要更复杂的功能，可以考虑：

1. **支持多字段查询**：同时查询多个字段的引用关系
2. **生成依赖图**：可视化配置表之间的依赖关系
3. **反向查询**：查询某个表引用了哪些其他表
4. **导出为JSON/XML**：支持更多输出格式
5. **集成到CI/CD**：在配置变更时自动生成引用报告

## 示例配置

在你的 `luban.conf` 中可以这样配置：

```json
{
  "dataTarget": "field-reference",
  "options": {
    "field-reference.targetTable": "TbItem",
    "field-reference.targetField": "Id",
    "field-reference.outputFile": "reports/item_references.txt"
  }
}
```

或者使用代码生成：

```json
{
  "codeTarget": "cs-field-reference",
  "options": {
    "cs-field-reference.targetTable": "TbItem",
    "cs-field-reference.targetField": "Id"
  }
}
```

