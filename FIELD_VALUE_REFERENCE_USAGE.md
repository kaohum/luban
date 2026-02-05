# 字段值引用追踪功能 - 使用说明

## 功能概述

这个功能可以追踪配置表中某个字段的**所有值**分别被哪些地方引用。与类型级别的追踪不同，这是**数据级别**的引用追踪。

## 核心特性

- ✅ **数据级别追踪** - 追踪具体的字段值，而不是字段类型
- ✅ **批量分析** - 一次性分析目标表所有记录的引用情况
- ✅ **性能优化** - 只遍历一次所有数据，同时检查所有目标值
- ✅ **递归检查** - 自动检查嵌套结构（Bean、List、Array、Map）
- ✅ **详细报告** - 显示引用的表名、记录ID、字段路径等详细信息

## 使用方式

### 方式一：生成文本报告（DataTarget）

```bash
dotnet Luban.dll \
  --conf luban.conf \
  --dataTarget field-value-reference \
  -x field-value-reference.targetTable=TbProperty \
  -x field-value-reference.targetField=Key
```

**参数说明：**
- `targetTable`: 目标表名（必填）
- `targetField`: 目标字段名（必填）
- `outputFile`: 输出文件名（可选，默认为 `field_value_reference_{表名}_{字段名}.txt`）

### 方式二：生成 C# 代码（CodeTarget）

```bash
dotnet Luban.dll \
  --conf luban.conf \
  --codeTarget cs-field-value-reference \
  -x cs-field-value-reference.targetTable=TbProperty \
  -x cs-field-value-reference.targetField=Key
```

## 使用示例

### 示例配置

```xml
<!-- 枚举定义 -->
<enum name="PropertyType">
    <var name="Health" value="1"/>
    <var name="Attack" value="2"/>
    <var name="Defense" value="3"/>
</enum>

<!-- 目标表 -->
<bean name="Property">
    <field name="Key" type="PropertyType"/>
    <field name="Value" type="int"/>
</bean>
<table name="TbProperty" value="Property" index="Key"/>

<!-- 目标表数据 -->
<var key="Health" value="100"/>
<var key="Attack" value="50"/>
<var key="Defense" value="30"/>

<!-- 其他表 -->
<bean name="Item">
    <field name="Id" type="int"/>
    <field name="Type" type="PropertyType"/>
    <field name="BonusTypes" type="list,PropertyType"/>
</bean>
<table name="TbItem" value="Item" index="Id"/>

<!-- 其他表数据 -->
<var id="1001" type="Health" bonus_types="Health,Attack"/>
<var id="1002" type="Attack" bonus_types="Defense"/>
```

### 输出示例（文本报告）

```
# 字段值引用分析报告
# 目标表: TbProperty
# 目标字段: Key
# 生成时间: 2025-02-05 18:00:00

# 总体统计:
# - 目标值数量: 3
# - 引用该字段的表数量: 1
# - 引用该字段的记录数量: 4

# ================================================================================
# 目标值: Health (记录ID: 1)
# ================================================================================
# 引用该值的记录数量: 2

# 格式: 表名 | 记录ID | 字段名 | 字段路径 | 字段值
# --------------------------------------------------------------------------------
TbItem | 1001 | Type | Type | Health
TbItem | 1001 | BonusTypes | BonusTypes[0] | Health

# ================================================================================
# 目标值: Attack (记录ID: 2)
# ================================================================================
# 引用该值的记录数量: 2

# 格式: 表名 | 记录ID | 字段名 | 字段路径 | 字段值
# --------------------------------------------------------------------------------
TbItem | 1002 | Type | Type | Attack
TbItem | 1001 | BonusTypes | BonusTypes[1] | Attack

# ================================================================================
# 目标值: Defense (记录ID: 3)
# ================================================================================
# 引用该值的记录数量: 1

# 格式: 表名 | 记录ID | 字段名 | 字段路径 | 字段值
# --------------------------------------------------------------------------------
TbItem | 1002 | BonusTypes | BonusTypes[0] | Defense
```

### 生成的 C# 代码使用示例

```csharp
using YourNamespace;

// 获取所有目标值信息
var allValues = FieldValueReference_TbProperty_Key.GetAllTargetValues();
foreach (var target in allValues)
{
    Console.WriteLine($"值: {target.Value}, 引用数: {target.References.Count}");
}

// 查询特定值的引用
var healthRefs = FieldValueReference_TbProperty_Key.GetReferencesByValue("Health");
if (healthRefs != null)
{
    Console.WriteLine($"Health 被引用了 {healthRefs.References.Count} 次");
    foreach (var reference in healthRefs.References)
    {
        Console.WriteLine($"  - {reference.TableName}.{reference.RecordId}.{reference.FieldPath}");
    }
}

// 检查某个值是否被引用
if (FieldValueReference_TbProperty_Key.HasReferences("Attack"))
{
    Console.WriteLine("Attack 值正在被使用，不能删除");
}

// 获取所有未被引用的值
var unused = FieldValueReference_TbProperty_Key.GetUnreferencedValues();
Console.WriteLine($"未使用的值: {string.Join(", ", unused)}");

// 获取指定表的所有引用
var itemRefs = FieldValueReference_TbProperty_Key.GetReferencesByTable("TbItem");
Console.WriteLine($"TbItem 表中的引用数: {itemRefs.Count()}");
```

## 支持的数据类型

系统会递归检查以下所有数据类型：

- **基础类型**: int, long, string, float, double, bool, byte, short
- **枚举类型**: enum
- **复杂类型**: bean
- **集合类型**: array, list, set
- **映射类型**: map

## 嵌套结构支持

系统会自动递归检查嵌套结构：

```xml
<!-- 嵌套 Bean -->
<bean name="Reward">
    <field name="Type" type="PropertyType"/>  <!-- 会被检测到 -->
</bean>

<bean name="Quest">
    <field name="Rewards" type="list,Reward"/>  <!-- Reward 内部的 Type 会被检测到 -->
</bean>

<!-- 嵌套集合 -->
<bean name="Item">
    <field name="Properties" type="map,int,PropertyType"/>  <!-- Map 值会被检测到 -->
</bean>
```

## 性能优化

### 优化策略

1. **单次遍历** - 只遍历一次所有数据，同时检查所有目标值
2. **索引优化** - 使用 Dictionary 快速查找目标值
3. **延迟计算** - 只在需要时才计算统计信息

### 时间复杂度

- **目标值索引**: O(n) - n 为目标表记录数
- **数据遍历**: O(m) - m 为所有表的总记录数
- **总体复杂度**: O(n + m)

### 适用规模

- ✅ 小型项目（< 1000 条记录）：毫秒级
- ✅ 中型项目（1000-10000 条记录）：秒级
- ✅ 大型项目（> 10000 条记录）：可能需要几秒到几十秒

## 使用场景

### 1. 数据清理

查找未被引用的枚举值，可以安全删除：

```csharp
var unused = FieldValueReference_TbProperty_Key.GetUnreferencedValues();
Console.WriteLine($"可以删除的值: {string.Join(", ", unused)}");
```

### 2. 删除前检查

删除配置前检查是否有引用：

```csharp
public bool CanDeleteProperty(string key)
{
    return !FieldValueReference_TbProperty_Key.HasReferences(key);
}
```

### 3. 影响范围分析

修改配置前了解影响范围：

```csharp
var refs = FieldValueReference_TbProperty_Key.GetReferencesByValue("Health");
Console.WriteLine($"修改 Health 会影响 {refs.References.Count} 个地方");
```

### 4. 数据完整性验证

验证所有引用的值都存在：

```csharp
var allValues = FieldValueReference_TbProperty_Key.GetAllTargetValues();
var referencedValues = allValues.Where(t => t.References.Count > 0).Select(t => t.Value);
// 检查是否有引用了不存在的值
```

## 注意事项

1. **需要加载数据** - 此功能需要加载所有配置数据，确保数据文件可访问
2. **值比较是精确的** - 使用字符串比较，区分大小写
3. **性能考虑** - 对于大型项目，分析可能需要一些时间
4. **内存占用** - 会在内存中保存所有引用信息

## 与旧版本的区别

| 特性 | 旧版 field-reference | 新版 field-value-reference |
|------|---------------------|---------------------------|
| 检测级别 | 类型级别 | 数据级别 |
| 输入 | 表名 + 字段名 | 表名 + 字段名 |
| 输出 | 使用该类型的字段 | 引用该值的具体数据 |
| 性能 | 快（只检查类型） | 较慢（需要遍历数据） |
| 用途 | 类型依赖分析 | 数据引用追踪 |

## 故障排除

### 问题：未找到任何引用

**可能原因**：
1. 目标字段的值确实没有被引用
2. 数据类型不匹配（如字符串 "1" vs 整数 1）
3. 数据还未加载

**解决方案**：
- 检查数据文件是否正确
- 确认字段类型定义
- 查看生成的报告中的目标值列表

### 问题：性能太慢

**可能原因**：
- 配置表数据量太大
- 嵌套结构太深

**解决方案**：
- 只在需要时运行分析
- 考虑分批处理
- 优化配置表结构

## 总结

字段值引用追踪功能提供了强大的数据级别引用分析能力，可以帮助您：

- ✅ 了解每个配置值的使用情况
- ✅ 安全地删除未使用的配置
- ✅ 评估配置变更的影响范围
- ✅ 验证数据完整性

这是配置管理的重要工具，特别适合在重构和清理配置时使用。

