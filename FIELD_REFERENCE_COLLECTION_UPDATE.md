# 字段引用追踪功能 - 集合类型检测更新

## 🔄 功能增强（2025-02-05 v2）

### 新增功能：集合类型元素检测

现在支持检测集合类型（Array、List、Set、Map）的元素类型是否引用了目标类型。

### 支持的检测方式（共5种）

#### 1. ref 标签检测（原有）
```xml
<field name="ItemId" type="int" ref="TbItem"/>
```

#### 2. 枚举类型检测（v1）
```xml
<field name="Type" type="PropertyType"/>
```

#### 3. Bean 类型检测（v1）
```xml
<field name="Config" type="ItemConfig"/>
```

#### 4. 集合元素类型检测（v2 新增）
```xml
<!-- 目标字段 -->
<field name="Key" type="PropertyType"/>  <!-- PropertyType 是枚举 -->

<!-- 会被检测到的情况 -->
<field name="Types" type="list,PropertyType"/>      <!-- List 元素 -->
<field name="TypeArray" type="array,PropertyType"/> <!-- Array 元素 -->
<field name="TypeSet" type="set,PropertyType"/>     <!-- Set 元素 -->
```

#### 5. Map 键值类型检测（v2 新增）
```xml
<!-- Map 的键类型 -->
<field name="TypeMap" type="map,PropertyType,int"/>  <!-- 键是 PropertyType -->

<!-- Map 的值类型 -->
<field name="ConfigMap" type="map,int,PropertyType"/>  <!-- 值是 PropertyType -->
```

### 完整示例

假设有以下配置：

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

<!-- 其他表 -->
<bean name="Item">
    <field name="Id" type="int"/>
    <field name="Type" type="PropertyType"/>                    <!-- 直接使用 ✓ -->
    <field name="BonusTypes" type="list,PropertyType"/>         <!-- List 元素 ✓ -->
    <field name="RequiredTypes" type="array,PropertyType"/>     <!-- Array 元素 ✓ -->
    <field name="UniqueTypes" type="set,PropertyType"/>         <!-- Set 元素 ✓ -->
    <field name="TypeValues" type="map,PropertyType,int"/>      <!-- Map 键 ✓ -->
    <field name="ValueTypes" type="map,int,PropertyType"/>      <!-- Map 值 ✓ -->
</bean>
<table name="TbItem" value="Item" index="Id"/>

<!-- 嵌套结构 -->
<bean name="Reward">
    <field name="Type" type="PropertyType"/>                    <!-- Bean 内部 ✓ -->
    <field name="Types" type="list,PropertyType"/>              <!-- Bean 内部的 List ✓ -->
</bean>

<bean name="Quest">
    <field name="Rewards" type="list,Reward"/>                  <!-- 嵌套在 List<Bean> 中 ✓ -->
</bean>
<table name="TbQuest" value="Quest" index="Id"/>
```

### 运行命令

```bash
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbProperty \
  -x field-reference.targetField=Key
```

### 输出结果

```
# 字段引用分析报告
# 目标表: TbProperty
# 目标字段: Key
# 目标字段类型: PropertyType
# 生成时间: 2025-02-05 16:00:00

# 统计信息:
# - 引用该字段的表数量: 2
# - 引用该字段的字段数量: 8

# 引用详情:
# 格式: 表名 | 字段名 | 字段类型 | 字段路径
# --------------------------------------------------------------------------------
TbItem | Type | enum | Type
TbItem | BonusTypes | list | BonusTypes
TbItem | RequiredTypes | array | RequiredTypes
TbItem | UniqueTypes | set | UniqueTypes
TbItem | TypeValues | map | TypeValues
TbItem | ValueTypes | map | ValueTypes
TbQuest | Type | enum | Rewards.Type
TbQuest | Types | list | Rewards.Types
```

### 技术实现

#### 检测逻辑扩展

```csharp
// 方式4: 检查集合类型的元素类型
if (!isMatch && type.IsCollection && type.ElementType != null)
{
    var elementType = type.ElementType;
    
    // 检查元素类型是否是目标枚举
    if (elementType is TEnum elemEnumType)
    {
        string elemEnumFullName = elemEnumType.DefEnum.FullName;
        if (collection.TargetFieldType != null && elemEnumFullName == collection.TargetFieldType)
        {
            isMatch = true;
        }
    }
    // 检查元素类型是否是目标 Bean
    else if (elementType is TBean elemBeanType)
    {
        string elemBeanFullName = elemBeanType.DefBean.FullName;
        if (collection.TargetFieldType != null && elemBeanFullName == collection.TargetFieldType)
        {
            isMatch = true;
        }
    }
}

// 方式5: 检查 Map 类型的键值类型
if (!isMatch && type is TMap mapType)
{
    // 检查键类型
    if (mapType.KeyType is TEnum keyEnumType)
    {
        string keyEnumFullName = keyEnumType.DefEnum.FullName;
        if (collection.TargetFieldType != null && keyEnumFullName == collection.TargetFieldType)
        {
            isMatch = true;
        }
    }
    else if (mapType.KeyType is TBean keyBeanType)
    {
        string keyBeanFullName = keyBeanType.DefBean.FullName;
        if (collection.TargetFieldType != null && keyBeanFullName == collection.TargetFieldType)
        {
            isMatch = true;
        }
    }
    
    // 检查值类型
    if (!isMatch)
    {
        if (mapType.ValueType is TEnum valueEnumType)
        {
            string valueEnumFullName = valueEnumType.DefEnum.FullName;
            if (collection.TargetFieldType != null && valueEnumFullName == collection.TargetFieldType)
            {
                isMatch = true;
            }
        }
        else if (mapType.ValueType is TBean valueBeanType)
        {
            string valueBeanFullName = valueBeanType.DefBean.FullName;
            if (collection.TargetFieldType != null && valueBeanFullName == collection.TargetFieldType)
            {
                isMatch = true;
            }
        }
    }
}
```

### 支持的集合类型

| 集合类型 | 检测内容 | 示例 |
|---------|---------|------|
| Array | 元素类型 | `array,PropertyType` |
| List | 元素类型 | `list,PropertyType` |
| Set | 元素类型 | `set,PropertyType` |
| Map | 键类型和值类型 | `map,PropertyType,int` 或 `map,int,PropertyType` |

### 嵌套检测

由于使用了 `DataActionHelpVisitor2`，所有嵌套结构都会被自动递归检测：

```xml
<!-- 多层嵌套示例 -->
<bean name="Level1">
    <field name="Types" type="list,PropertyType"/>  <!-- 会被检测到 -->
</bean>

<bean name="Level2">
    <field name="Items" type="list,Level1"/>  <!-- Level1 内部的 Types 会被检测到 -->
</bean>

<bean name="Level3">
    <field name="Groups" type="map,int,Level2"/>  <!-- Level2 内部的 Items 内部的 Types 会被检测到 -->
</bean>
```

### 修改的文件

1. ✅ `src/Luban.DataTarget.Builtin/FieldReference/FieldReferenceCollectorVisitor.cs`
   - 添加集合元素类型检测（方式4）
   - 添加 Map 键值类型检测（方式5）

2. ✅ `src/Luban.CSharp/CodeTarget/CsharpFieldReferenceCodeTarget.cs`
   - 同步所有更新

### 编译状态

```
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

### 优势

1. **全面覆盖**：支持所有集合类型的元素检测
2. **Map 支持**：同时检测 Map 的键和值类型
3. **自动递归**：嵌套结构自动处理
4. **精确匹配**：使用完整类型名称比较

### 使用场景

#### 场景1：查找枚举在集合中的使用
```bash
# 查找 PropertyType 枚举在所有集合中的使用
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbProperty \
  -x field-reference.targetField=Key
```

#### 场景2：查找 Bean 在集合中的使用
```bash
# 查找 ItemConfig Bean 在所有集合中的使用
dotnet Luban.dll \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Config
```

#### 场景3：重构枚举前的影响评估
在重构枚举类型前，使用此功能可以：
1. 找到所有直接使用该枚举的字段
2. 找到所有在 List/Array/Set 中使用该枚举的字段
3. 找到所有在 Map 的键或值中使用该枚举的字段
4. 找到所有在嵌套 Bean 中使用该枚举的字段

### 注意事项

1. **类型匹配是精确的**：使用 `FullName` 进行比较
2. **只检测类型定义**：不检查实际数据值
3. **性能影响**：增加了类型检查，但影响很小
4. **向后兼容**：所有原有功能保持不变

### 完整的检测能力总结

现在系统可以检测：

✅ **直接引用**
- ref 标签引用
- 枚举类型使用
- Bean 类型使用

✅ **集合引用**
- Array 元素类型
- List 元素类型
- Set 元素类型
- Map 键类型
- Map 值类型

✅ **嵌套引用**
- Bean 内部字段
- 集合内部的 Bean
- Bean 内部的集合
- 多层嵌套结构

这使得字段引用追踪功能非常强大，可以全面分析配置表之间的依赖关系！

## 总结

这次更新进一步增强了字段引用追踪的能力，现在可以：
- ✅ 检测集合类型的元素是否使用了目标类型
- ✅ 检测 Map 的键和值是否使用了目标类型
- ✅ 自动递归处理所有嵌套结构
- ✅ 提供完整的依赖关系分析

这对于重构枚举类型、Bean 结构或评估配置变更影响非常有用！

