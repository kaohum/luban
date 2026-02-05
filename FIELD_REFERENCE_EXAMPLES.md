# 字段引用追踪功能示例

## 示例1：基础配置表定义

假设你有以下配置表定义：

### TbItem.xml (物品表)
```xml
<bean name="Item">
    <field name="Id" type="int"/>
    <field name="Name" type="string"/>
    <field name="Type" type="int"/>
    <field name="Price" type="int"/>
</bean>

<table name="TbItem" value="Item" index="Id"/>
```

### TbQuest.xml (任务表)
```xml
<bean name="Quest">
    <field name="Id" type="int"/>
    <field name="Name" type="string"/>
    <field name="RewardItemId" type="int" ref="TbItem"/>
    <field name="RequiredItemId" type="int" ref="TbItem"/>
    <field name="RequiredItemCount" type="int"/>
</bean>

<table name="TbQuest" value="Quest" index="Id"/>
```

### TbShop.xml (商店表)
```xml
<bean name="ShopItem">
    <field name="ItemId" type="int" ref="TbItem"/>
    <field name="Price" type="int"/>
    <field name="Stock" type="int"/>
</bean>

<bean name="Shop">
    <field name="Id" type="int"/>
    <field name="Name" type="string"/>
    <field name="Items" type="list,ShopItem"/>
</bean>

<table name="TbShop" value="Shop" index="Id"/>
```

## 示例2：查询 TbItem 的引用

### 使用 DataTarget 生成文本报告

```bash
dotnet Luban.dll \
  --conf luban.conf \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Id
```

**输出结果 (field_reference_TbItem_Id.txt):**
```
# 字段引用分析报告
# 目标表: TbItem
# 目标字段: Id
# 生成时间: 2025-02-05 10:30:00

# 统计信息:
# - 引用该字段的表数量: 2
# - 引用该字段的字段数量: 3

# 引用详情:
# 格式: 表名 | 字段名 | 字段类型 | 字段路径
# --------------------------------------------------------------------------------
TbQuest | RewardItemId | int | RewardItemId
TbQuest | RequiredItemId | int | RequiredItemId
TbShop | ItemId | int | Items.ItemId
```

### 使用 CodeTarget 生成 C# 代码

```bash
dotnet Luban.dll \
  --conf luban.conf \
  --codeTarget cs-field-reference \
  -x cs-field-reference.targetTable=TbItem \
  -x cs-field-reference.targetField=Id
```

**生成的代码 (FieldReference_TbItem_Id.cs):**
```csharp
using System;
using System.Collections.Generic;

namespace YourNamespace
{
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
            public const int TableCount = 2;
            public const int FieldCount = 3;
        }
        
        private static readonly List<ReferenceInfo> _references = new List<ReferenceInfo>
        {
            new ReferenceInfo
            {
                TableName = "TbQuest",
                FieldName = "RewardItemId",
                FieldType = "int",
                FieldPath = "RewardItemId"
            },
            new ReferenceInfo
            {
                TableName = "TbQuest",
                FieldName = "RequiredItemId",
                FieldType = "int",
                FieldPath = "RequiredItemId"
            },
            new ReferenceInfo
            {
                TableName = "TbShop",
                FieldName = "ItemId",
                FieldType = "int",
                FieldPath = "Items.ItemId"
            },
        };
        
        public static IReadOnlyList<ReferenceInfo> GetAllReferences()
        {
            return _references.AsReadOnly();
        }
        
        public static IEnumerable<ReferenceInfo> GetReferencesByTable(string tableName)
        {
            foreach (var reference in _references)
            {
                if (reference.TableName == tableName)
                {
                    yield return reference;
                }
            }
        }
        
        public static bool HasReference(string tableName)
        {
            foreach (var reference in _references)
            {
                if (reference.TableName == tableName)
                {
                    return true;
                }
            }
            return false;
        }
        
        public static IEnumerable<string> GetReferencingTables()
        {
            var tables = new HashSet<string>();
            foreach (var reference in _references)
            {
                if (tables.Add(reference.TableName))
                {
                    yield return reference.TableName;
                }
            }
        }
    }
}
```

## 示例3：在业务代码中使用

```csharp
using System;
using YourNamespace;

public class ItemManager
{
    // 删除物品前检查引用
    public bool CanDeleteItem(int itemId)
    {
        // 检查是否有其他表引用了这个物品
        var references = FieldReference_TbItem_Id.GetAllReferences();
        
        Console.WriteLine($"物品 {itemId} 被以下表引用:");
        foreach (var reference in references)
        {
            Console.WriteLine($"  - {reference.TableName}.{reference.FieldName}");
        }
        
        // 如果有引用，不允许删除
        return references.Count == 0;
    }
    
    // 获取物品的影响范围
    public void ShowItemImpact(int itemId)
    {
        Console.WriteLine($"物品 {itemId} 的影响范围:");
        Console.WriteLine($"  引用表数量: {FieldReference_TbItem_Id.Statistics.TableCount}");
        Console.WriteLine($"  引用字段数量: {FieldReference_TbItem_Id.Statistics.FieldCount}");
        
        Console.WriteLine("\n引用详情:");
        foreach (var tableName in FieldReference_TbItem_Id.GetReferencingTables())
        {
            Console.WriteLine($"\n  表: {tableName}");
            foreach (var reference in FieldReference_TbItem_Id.GetReferencesByTable(tableName))
            {
                Console.WriteLine($"    - 字段: {reference.FieldName} (路径: {reference.FieldPath})");
            }
        }
    }
    
    // 验证配置完整性
    public void ValidateItemReferences()
    {
        var itemTable = ConfigManager.Instance.GetTable<TbItem>();
        var references = FieldReference_TbItem_Id.GetAllReferences();
        
        foreach (var reference in references)
        {
            Console.WriteLine($"验证 {reference.TableName}.{reference.FieldName} 的引用...");
            
            // 这里可以添加实际的验证逻辑
            // 例如：检查引用的 ItemId 是否在 TbItem 中存在
        }
    }
}
```

## 示例4：复杂嵌套结构

### 配置定义
```xml
<bean name="RewardItem">
    <field name="ItemId" type="int" ref="TbItem"/>
    <field name="Count" type="int"/>
</bean>

<bean name="RewardGroup">
    <field name="Items" type="list,RewardItem"/>
    <field name="Gold" type="int"/>
</bean>

<bean name="Quest">
    <field name="Id" type="int"/>
    <field name="Rewards" type="RewardGroup"/>
</bean>

<table name="TbQuest" value="Quest" index="Id"/>
```

### 查询结果
```
# 引用详情:
TbQuest | ItemId | int | Rewards.Items.ItemId
```

注意字段路径显示了完整的嵌套路径：`Rewards.Items.ItemId`

## 示例5：批量查询脚本

创建一个脚本来批量查询多个表的引用关系：

```bash
#!/bin/bash

# 查询多个表的引用关系
tables=("TbItem" "TbSkill" "TbBuff" "TbMonster")

for table in "${tables[@]}"
do
    echo "正在分析 $table 的引用关系..."
    dotnet Luban.dll \
      --conf luban.conf \
      --dataTarget field-reference \
      -x field-reference.targetTable=$table \
      -x field-reference.targetField=Id \
      -x field-reference.outputFile=reports/${table}_references.txt
done

echo "所有分析完成！"
```

## 示例6：集成到构建流程

在 CI/CD 中自动生成引用报告：

```yaml
# .github/workflows/config-analysis.yml
name: Config Analysis

on:
  push:
    paths:
      - 'config/**'

jobs:
  analyze:
    runs-on: ubuntu-latest
    steps:
      - uses: actions/checkout@v2
      
      - name: Setup .NET
        uses: actions/setup-dotnet@v1
        with:
          dotnet-version: '8.0.x'
      
      - name: Generate Field References
        run: |
          dotnet Luban.dll \
            --conf luban.conf \
            --dataTarget field-reference \
            -x field-reference.targetTable=TbItem \
            -x field-reference.targetField=Id
      
      - name: Upload Reports
        uses: actions/upload-artifact@v2
        with:
          name: field-references
          path: field_reference_*.txt
```

## 总结

这个字段引用追踪功能可以帮助你：

1. **理解配置依赖**：清楚地看到配置表之间的引用关系
2. **安全重构**：在修改配置前了解影响范围
3. **自动化工具**：生成代码用于运行时查询和验证
4. **文档生成**：自动生成配置依赖文档

通过结合 DataTarget 和 CodeTarget，你可以同时获得人类可读的报告和机器可用的代码。

