# 字段引用追踪功能 - 重新设计方案

## 需求分析

### 原需求（类型级别）
- 输入：表名 + 字段名
- 输出：所有使用了该字段**类型**的地方
- 示例：查找所有使用 `PropertyType` 枚举的字段

### 新需求（数据级别）⭐
- 输入：表名 + 字段名 + **字段值**
- 输出：所有在数据中引用了该**具体值**的记录
- 示例：查找所有引用了 `PropertyType.Health` 这个值的数据

## 设计方案

### 方案一：完全重构（推荐）
创建一个新的 DataTarget：`field-value-reference`

**特点**：
- 专注于数据级别的引用追踪
- 需要加载实际数据
- 比较数据值而不是类型

**使用方式**：
```bash
dotnet Luban.dll \
  --dataTarget field-value-reference \
  -x field-value-reference.targetTable=TbProperty \
  -x field-value-reference.targetField=Key \
  -x field-value-reference.targetValue=Health
```

### 方案二：扩展现有功能
在现有的 `field-reference` 基础上添加可选的值过滤

**使用方式**：
```bash
# 类型级别（现有功能）
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbProperty \
  -x field-reference.targetField=Key

# 数据级别（新功能）
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbProperty \
  -x field-reference.targetField=Key \
  -x field-reference.targetValue=Health  # 添加值过滤
```

## 推荐方案：方案一（新建 DataTarget）

### 原因
1. **职责分离**：类型追踪和数据追踪是两个不同的功能
2. **性能考虑**：数据级别需要遍历所有数据，性能开销更大
3. **易于维护**：独立的实现更清晰
4. **灵活性**：可以针对数据追踪做专门优化

### 实现要点

#### 1. 数据比较逻辑
```csharp
// 比较两个数据值是否相等
bool IsValueEqual(DType data1, DType data2)
{
    if (data1 is DInt int1 && data2 is DInt int2)
        return int1.Value == int2.Value;
    
    if (data1 is DString str1 && data2 is DString str2)
        return str1.Value == str2.Value;
    
    if (data1 is DEnum enum1 && data2 is DEnum enum2)
        return enum1.Value == enum2.Value;
    
    // ... 其他类型
}
```

#### 2. 遍历逻辑
```csharp
// 1. 获取目标值
var targetRecord = GetTargetRecord(targetTable, targetField, targetValue);
var targetData = targetRecord.Data.GetField(targetField);

// 2. 遍历所有表的所有记录
foreach (var table in tables)
{
    foreach (var record in GetRecords(table))
    {
        // 3. 遍历记录的所有字段
        foreach (var field in record.Data.Fields)
        {
            // 4. 递归检查字段值
            if (CheckValueRecursively(field, targetData))
            {
                // 找到引用
                AddReference(table, record, field);
            }
        }
    }
}
```

#### 3. 递归检查
```csharp
bool CheckValueRecursively(DType data, DType targetData)
{
    // 直接比较
    if (IsValueEqual(data, targetData))
        return true;
    
    // 检查集合
    if (data is DList list)
    {
        foreach (var item in list.Datas)
        {
            if (CheckValueRecursively(item, targetData))
                return true;
        }
    }
    
    // 检查 Bean
    if (data is DBean bean)
    {
        foreach (var field in bean.Fields)
        {
            if (CheckValueRecursively(field, targetData))
                return true;
        }
    }
    
    // 检查 Map
    if (data is DMap map)
    {
        foreach (var kvp in map.DataMap)
        {
            if (CheckValueRecursively(kvp.Key, targetData) || 
                CheckValueRecursively(kvp.Value, targetData))
                return true;
        }
    }
    
    return false;
}
```

### 输出格式

```
# 字段值引用分析报告
# 目标表: TbProperty
# 目标字段: Key
# 目标值: Health
# 生成时间: 2025-02-05 17:00:00

# 统计信息:
# - 引用该值的表数量: 3
# - 引用该值的记录数量: 15
# - 引用该值的字段数量: 8

# 引用详情:
# 格式: 表名 | 记录ID | 字段名 | 字段路径 | 字段值
# --------------------------------------------------------------------------------
TbItem | 1001 | Type | Type | Health
TbItem | 1002 | BonusTypes | BonusTypes[0] | Health
TbQuest | 2001 | RewardType | Rewards[0].Type | Health
TbQuest | 2002 | RequiredTypes | Requirements[1].Type | Health
TbShop | 3001 | ItemType | Items[0].Type | Health
```

## 实现步骤

1. ✅ 创建 `FieldValueReferenceCollection.cs` - 收集数据级别的引用
2. ✅ 创建 `FieldValueReferenceCollectorVisitor.cs` - 遍历数据并比较值
3. ✅ 创建 `FieldValueReferenceDataTarget.cs` - DataTarget 实现
4. ✅ 创建 `CsharpFieldValueReferenceCodeTarget.cs` - CodeTarget 实现（可选）
5. ✅ 创建 Scriban 模板

## 与现有功能的对比

| 特性 | field-reference (类型级别) | field-value-reference (数据级别) |
|------|---------------------------|--------------------------------|
| 输入 | 表名 + 字段名 | 表名 + 字段名 + 字段值 |
| 检测对象 | 字段类型定义 | 实际数据值 |
| 性能 | 快（只检查类型） | 慢（需要遍历所有数据） |
| 用途 | 类型依赖分析 | 数据引用追踪 |
| 示例 | 查找使用 PropertyType 的字段 | 查找引用 Health 值的数据 |

## 建议

我建议创建一个新的 `field-value-reference` DataTarget，原因：

1. **清晰的职责分离**
2. **更好的性能控制**
3. **独立的优化空间**
4. **不影响现有功能**

您觉得这个方案如何？如果同意，我立即开始实现。

