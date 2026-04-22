---
title: Bean 默认值填充设计
date: 2026-04-22
status: approved
---

# Bean 默认值填充设计

## 目标

当配置表中自定义 bean 类型的字段未完全填写时，未填写的字段自动使用其类型的默认值填充，而非报错。

例如：`CustomBean` 含 4 个字段 `Id(int), Type(enum), Num(int), Value(string)`，sep=','。配置 `"0"` 时，后 3 个字段分别取枚举 0 默认值、int 默认值 0、string 默认值 `""`。

## 设计决策

- **覆盖格式**：Excel/CSV + JSON + Lua/XML + Stream（全部格式）
- **空位处理**：中间空位也填默认值（`"0,,hello"` → Id=0, Type=0, Num=0, Value="hello"）
- **嵌套 bean**：递归填充，层层取默认值
- **多态 bean**：不支持部分填充，保持现有行为（类型标识缺失时报错）
- **Nullable 字段**：缺失时返回 `null`，而非类型默认值

## 架构

### 新增：`DefaultDataCreator`

新建 `Luban.DataLoader.Builtin\DataVisitors\DefaultDataCreator.cs`，实现 `ITypeFuncVisitor<DType>`。针对每个类型返回其默认值：

| 类型 | 默认值 |
|------|--------|
| TBool | `false` |
| TByte/TShort/TInt/TLong/TFloat/TDouble | `0` |
| TString | `""` |
| TEnum | `"0"` |
| TDateTime 等时间类型 | `"0"` 解析的实例 |
| TBean（非多态） | 递归为每个 HierarchyField 创建默认值 |
| TBean（多态/抽象） | 抛异常（无法确定具体子类） |
| TArray/TList/TSet | 空集合 |
| TMap | 空 map |
| Nullable 类型 | `null` |

### 修改点

#### 1. `ExcelStreamDataCreator.CreateBeanFields`

stream 耗尽时，剩余字段使用 `DefaultDataCreator` 填充：

```csharp
foreach (DefField f in bean.HierarchyFields)
{
    if (!stream.TryPeek(out _))
    {
        list.Add(DefaultDataCreator.Ins.Accept(f.CType));
    }
    else
    {
        list.Add(f.CType.Apply(this, stream));
    }
}
```

#### 2. `SheetDataCreator.CreateBeanFields`

`TryGetBeanField` 找不到对应列时，使用默认值：

```csharp
if (!TryGetBeanField(row, f, out var field))
{
    list.Add(DefaultDataCreator.Ins.Accept(f.CType));
}
else
{
    list.Add(f.CType.Apply(this, sheet, field));
}
```

记录 Info 级别日志说明哪个字段使用了默认值。

#### 3. `JsonDataCreator.Accept(TBean, ...)`

字段缺失时（非 nullable），使用 `DefaultDataCreator` 替代抛异常：

```csharp
else
{
    fields.Add(DefaultDataCreator.Ins.Accept(f.CType));
}
```
