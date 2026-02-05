# 字段引用追踪 - 快速开始

## 5分钟快速上手

### 步骤1：准备配置表

确保你的配置表中使用了 `ref` 标签标记引用关系：

```xml
<!-- TbQuest.xml -->
<bean name="Quest">
    <field name="Id" type="int"/>
    <field name="RewardItemId" type="int" ref="TbItem"/>
</bean>
```

### 步骤2：生成引用报告

```bash
# 查看哪些表引用了 TbItem.Id
dotnet Luban.dll \
  --conf luban.conf \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Id
```

### 步骤3：查看结果

打开生成的 `field_reference_TbItem_Id.txt` 文件：

```
# 字段引用分析报告
# 目标表: TbItem
# 目标字段: Id

# 统计信息:
# - 引用该字段的表数量: 2
# - 引用该字段的字段数量: 3

# 引用详情:
TbQuest | RewardItemId | int | RewardItemId
TbShop | ItemId | int | ItemId
```

## 生成代码版本

```bash
# 生成 C# 查询代码
dotnet Luban.dll \
  --conf luban.conf \
  --codeTarget cs-field-reference \
  -x cs-field-reference.targetTable=TbItem \
  -x cs-field-reference.targetField=Id
```

生成的代码可以在运行时使用：

```csharp
// 检查是否有引用
if (FieldReference_TbItem_Id.HasReference("TbQuest"))
{
    Console.WriteLine("TbQuest 引用了 TbItem.Id");
}

// 获取所有引用
var references = FieldReference_TbItem_Id.GetAllReferences();
```

## 常见问题

**Q: 为什么没有找到引用？**
A: 确保字段使用了 `ref` 标签，例如：`<field name="ItemId" type="int" ref="TbItem"/>`

**Q: 支持嵌套结构吗？**
A: 是的，支持 Bean、List、Array、Map 等嵌套结构。

**Q: 如何自定义输出文件名？**
A: 使用 `-x field-reference.outputFile=custom_name.txt` 参数。

## 更多信息

- 详细使用说明：[FIELD_REFERENCE_USAGE.md](FIELD_REFERENCE_USAGE.md)
- 示例代码：[FIELD_REFERENCE_EXAMPLES.md](FIELD_REFERENCE_EXAMPLES.md)
- 实现细节：[FIELD_REFERENCE_IMPLEMENTATION.md](FIELD_REFERENCE_IMPLEMENTATION.md)

