# 字段引用追踪功能 - 更新说明

## 🔄 功能增强（2025-02-05）

### 问题描述
初始版本只能检查带有 `ref` 标签的字段引用，无法检测到：
1. 枚举类型的使用
2. Bean 类型的使用
3. 嵌套结构中的类型使用

### 解决方案

#### 新增功能
现在支持三种引用检测方式：

1. **ref 标签检测**（原有功能）
   ```xml
   <field name="ItemId" type="int" ref="TbItem"/>
   ```

2. **枚举类型检测**（新增）
   - 自动检测目标字段的类型
   - 如果是枚举类型，查找所有使用该枚举的字段
   ```xml
   <!-- 目标字段 -->
   <field name="Key" type="PropertyType"/>  <!-- PropertyType 是枚举 -->
   
   <!-- 会被检测到的引用 -->
   <field name="Type" type="PropertyType"/>  <!-- 使用了相同的枚举 -->
   ```

3. **Bean 类型检测**（新增）
   - 如果目标字段是 Bean 类型，查找所有使用该 Bean 的字段
   ```xml
   <!-- 目标字段 -->
   <field name="Config" type="ItemConfig"/>  <!-- ItemConfig 是 Bean -->
   
   <!-- 会被检测到的引用 -->
   <field name="Settings" type="ItemConfig"/>  <!-- 使用了相同的 Bean -->
   ```

#### 嵌套结构支持
由于使用了 `DataActionHelpVisitor2`，自动支持在以下结构中递归检测：
- Bean 内部的字段
- List/Array 的元素类型
- Map 的键值类型
- Set 的元素类型

### 技术实现

#### 1. 新增属性
在 `FieldReferenceCollection` 中添加了 `TargetFieldType` 属性：
```csharp
public string TargetFieldType { get; set; }  // 目标字段的类型全名
```

#### 2. 类型获取逻辑
在开始分析前，先查找目标表和字段，获取其类型：
```csharp
var targetFieldDef = targetTableDef.ValueTType.DefBean.HierarchyFields
    .FirstOrDefault(f => f.Name == targetField);
if (targetFieldDef != null)
{
    var fieldType = targetFieldDef.CType;
    if (fieldType is TEnum enumType)
    {
        targetFieldType = enumType.DefEnum.FullName;
    }
    else if (fieldType is TBean beanType)
    {
        targetFieldType = beanType.DefBean.FullName;
    }
}
```

#### 3. 增强的引用检测
```csharp
private void CheckReference(DType data, TType type, FieldReferenceCollection collection, FieldReferenceContext context)
{
    bool isMatch = false;
    
    // 方式1: ref 标签检测
    if (type.HasTag("ref"))
    {
        string refTable = type.GetTag("ref");
        if (refTable == collection.TargetTable)
        {
            isMatch = true;
        }
    }
    
    // 方式2: 枚举类型检测
    if (!isMatch && type is TEnum enumType)
    {
        string enumFullName = enumType.DefEnum.FullName;
        if (collection.TargetFieldType != null && enumFullName == collection.TargetFieldType)
        {
            isMatch = true;
        }
    }
    
    // 方式3: Bean 类型检测
    if (!isMatch && type is TBean beanType)
    {
        string beanFullName = beanType.DefBean.FullName;
        if (collection.TargetFieldType != null && beanFullName == collection.TargetFieldType)
        {
            isMatch = true;
        }
    }
    
    if (isMatch)
    {
        collection.AddReference(...);
    }
}
```

### 使用示例

#### 示例1：查找枚举的使用
```bash
# 查找所有使用 PropertyType 枚举的地方
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbProperty \
  -x field-reference.targetField=Key
```

假设 `TbProperty.Key` 的类型是 `PropertyType` 枚举，系统会：
1. 识别出 `Key` 字段的类型是 `PropertyType`
2. 遍历所有表，查找类型为 `PropertyType` 的字段
3. 包括嵌套在 Bean、List、Array 中的字段

#### 示例2：查找 Bean 的使用
```bash
# 查找所有使用 ItemConfig Bean 的地方
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Config
```

#### 示例3：嵌套结构检测
```xml
<!-- 配置定义 -->
<bean name="RewardItem">
    <field name="Type" type="PropertyType"/>  <!-- 会被检测到 -->
    <field name="Count" type="int"/>
</bean>

<bean name="Quest">
    <field name="Rewards" type="list,RewardItem"/>
</bean>
```

即使 `PropertyType` 枚举嵌套在 `RewardItem` Bean 中，再嵌套在 `list` 中，也能被正确检测到。

### 输出示例

```
# 字段引用分析报告
# 目标表: TbProperty
# 目标字段: Key
# 目标字段类型: PropertyType
# 生成时间: 2025-02-05 15:30:00

# 统计信息:
# - 引用该字段的表数量: 5
# - 引用该字段的字段数量: 8

# 引用详情:
# 格式: 表名 | 字段名 | 字段类型 | 字段路径
# --------------------------------------------------------------------------------
TbQuest | RewardType | enum | RewardType
TbQuest | RequiredType | enum | Requirements.Type
TbShop | ItemType | enum | Items.Type
TbPlayer | DefaultType | enum | DefaultType
TbBag | FilterType | enum | Filters.Type
```

### 修改的文件

1. ✅ `src/Luban.DataTarget.Builtin/FieldReference/FieldReferenceCollection.cs`
   - 添加 `TargetFieldType` 属性

2. ✅ `src/Luban.DataTarget.Builtin/FieldReference/FieldReferenceCollectorVisitor.cs`
   - 增强 `CheckReference` 方法，支持枚举和 Bean 类型检测

3. ✅ `src/Luban.DataTarget.Builtin/FieldReference/FieldReferenceDataTarget.cs`
   - 添加目标字段类型获取逻辑

4. ✅ `src/Luban.CSharp/CodeTarget/CsharpFieldReferenceCodeTarget.cs`
   - 同步更新所有改动

### 编译状态

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 优势

1. **更全面的检测**：不仅检测 ref 标签，还检测类型匹配
2. **自动类型识别**：无需手动指定类型，自动从目标字段获取
3. **递归检测**：自动处理嵌套结构
4. **向后兼容**：原有的 ref 标签检测仍然有效

### 注意事项

1. **类型匹配是精确匹配**：使用完整的类型名称（FullName）进行比较
2. **基础类型不支持**：对于 int、string 等基础类型，只能通过 ref 标签检测
3. **性能影响**：由于需要遍历所有字段并检查类型，可能会稍微增加处理时间

### 下一步建议

1. **添加日志输出**：显示检测到的目标字段类型
2. **支持通配符**：支持查找多个相关类型
3. **性能优化**：缓存类型信息，避免重复查询
4. **可视化输出**：生成依赖关系图

## 总结

这次更新大大增强了字段引用追踪的能力，现在可以：
- ✅ 检测枚举类型的所有使用位置
- ✅ 检测 Bean 类型的所有使用位置
- ✅ 递归检测嵌套结构中的类型使用
- ✅ 保持向后兼容性

这使得该功能更加实用，特别是在重构枚举类型或 Bean 结构时，可以快速了解影响范围。

