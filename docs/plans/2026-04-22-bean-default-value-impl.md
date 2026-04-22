# Bean 默认值填充 Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** 在导表工具中为自定义 bean 类型添加部分填充支持，未填写的字段自动使用其类型的默认值。

**Architecture:** 新增 `DefaultDataCreator` visitor 为任意类型生成默认值，在 3 个 DataCreator 的 bean 字段创建逻辑中，缺失字段时委派给 `DefaultDataCreator`。

**Tech Stack:** C#, Luban 类型系统 visitor 模式

---

## Task 1: 创建 `DefaultDataCreator` Visitor

**Files:**
- Create: `src\Luban.DataLoader.Builtin\DataVisitors\DefaultDataCreator.cs`

**步骤 1: 创建 DefaultDataCreator.cs**

```csharp
// Copyright 2025 Code Philosophy
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using Luban.Datas;
using Luban.Defs;
using Luban.Types;
using Luban.TypeVisitors;

namespace Luban.DataLoader.Builtin.DataVisitors;

class DefaultDataCreator : ITypeFuncVisitor<DType>
{
    public static DefaultDataCreator Ins { get; } = new();

    public DType Accept(TBool type)
    {
        return type.IsNullable ? null : DBool.ValueOf(false);
    }

    public DType Accept(TByte type)
    {
        return type.IsNullable ? null : DByte.Default;
    }

    public DType Accept(TShort type)
    {
        return type.IsNullable ? null : DShort.Default;
    }

    public DType Accept(TInt type)
    {
        return type.IsNullable ? null : DInt.Default;
    }

    public DType Accept(TLong type)
    {
        return type.IsNullable ? null : DLong.Default;
    }

    public DType Accept(TFloat type)
    {
        return type.IsNullable ? null : DFloat.Default;
    }

    public DType Accept(TDouble type)
    {
        return type.IsNullable ? null : DDouble.Default;
    }

    public DType Accept(TEnum type)
    {
        if (type.IsNullable)
        {
            return null;
        }
        return new DEnum(type, "0");
    }

    public DType Accept(TString type)
    {
        if (type.IsNullable)
        {
            return null;
        }
        return DString.ValueOf(type, "");
    }

    public DType Accept(TDateTime type)
    {
        return type.IsNullable ? null : DataUtil.CreateDateTime("0");
    }

    public DType Accept(TDay type)
    {
        return type.IsNullable ? null : DataUtil.CreateDay("0");
    }

    public DType Accept(THour type)
    {
        return type.IsNullable ? null : DataUtil.CreateHour("0");
    }

    public DType Accept(TMinute type)
    {
        return type.IsNullable ? null : DataUtil.CreateMinute("0");
    }

    public DType Accept(TSecond type)
    {
        return type.IsNullable ? null : DataUtil.CreateSecond("0");
    }

    public DType Accept(TMillisecond type)
    {
        return type.IsNullable ? null : DataUtil.CreateMillisecond("0");
    }

    public DType Accept(TBean type)
    {
        if (type.IsNullable)
        {
            return null;
        }

        var originBean = type.DefBean;
        if (originBean.IsAbstractType)
        {
            throw new System.Exception($"结构:'{originBean.FullName}' 是多态类型，无法创建默认值");
        }

        var fields = new List<DType>();
        foreach (DefField f in originBean.HierarchyFields)
        {
            fields.Add(f.CType.Apply(this));
        }
        return new DBean(type, originBean, fields);
    }

    public DType Accept(TArray type)
    {
        return new DArray(type, new List<DType>());
    }

    public DType Accept(TList type)
    {
        return new DList(type, new List<DType>());
    }

    public DType Accept(TSet type)
    {
        return new DSet(type, new HashSet<DType>());
    }

    public DType Accept(TMap type)
    {
        return new DMap(type, new Dictionary<DType, DType>());
    }
}
```

**步骤 2: 编译验证**

```bash
cd E:\Projects\luban\src && dotnet build Luban.sln
```

预期：无错误

**步骤 3: 提交**

```bash
git add src/Luban.DataLoader.Builtin/DataVisitors/DefaultDataCreator.cs
git commit -m "feat: 添加 DefaultDataCreator visitor 生成类型默认值"
```

---

## Task 2: 修改 `ExcelStreamDataCreator` 支持部分填充

**Files:**
- Modify: `src\Luban.DataLoader.Builtin\DataVisitors\ExcelStreamDataCreator.cs:254-284`

**步骤 1: 修改 `CreateBeanFields` 方法**

找到第 254-284 行的 `CreateBeanFields` 方法，替换为：

```csharp
private List<DType> CreateBeanFields(DefBean bean, ExcelStream stream)
{
    var list = new List<DType>();
    foreach (DefField f in bean.HierarchyFields)
    {
        try
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
        catch (DataCreateException dce)
        {
            dce.Push(bean, f);
            throw;
        }
        catch (Exception e)
        {
            var dce = new DataCreateException(e, stream.LastReadDataInfo);
            dce.Push(bean, f);
            throw dce;
        }
    }
    return list;
}
```

**步骤 2: 编译验证**

```bash
cd E:\Projects\luban\src && dotnet build Luban.sln
```

**步骤 3: 提交**

```bash
git commit -am "feat: ExcelStreamDataCreator 支持 bean 部分填充"
```

---

## Task 3: 修改 `SheetDataCreator` 支持缺失列

**Files:**
- Modify: `src\Luban.DataLoader.Builtin\DataVisitors\SheetDataCreator.cs:402-431`

**步骤 1: 修改 `CreateBeanFields` 方法**

找到第 402-431 行的 `CreateBeanFields` 方法，替换为：

```csharp
private List<DType> CreateBeanFields(DefBean bean, RowColumnSheet sheet, TitleRow row)
{
    var list = new List<DType>();
    foreach (DefField f in bean.HierarchyFields)
    {
        string fname = f.Name;
        if (!TryGetBeanField(row, f, out var field))
        {
            s_logger.Info("bean:'{0}' 列:'{1}' 缺失，使用默认值", bean.FullName, fname);
            list.Add(DefaultDataCreator.Ins.Accept(f.CType));
            continue;
        }
        try
        {
            var v = f.CType.Apply(this, sheet, field);
            list.Add(v);
        }
        catch (DataCreateException dce)
        {
            dce.Push(bean, f);
            throw;
        }
        catch (Exception e)
        {
            s_logger.Info("error: {} \n {}", e.Message, e.StackTrace);
            var dce = new DataCreateException(e, $"Sheet:{sheet.SheetName} 字段:{fname} 位置:{field.Location}");
            dce.Push(bean, f);
            throw dce;
        }
    }
    return list;
}
```

**步骤 2: 编译验证**

```bash
cd E:\Projects\luban\src && dotnet build Luban.sln
```

**步骤 3: 提交**

```bash
git commit -am "feat: SheetDataCreator 支持缺失列使用默认值"
```

---

## Task 4: 修改 `JsonDataCreator` 支持缺失字段

**Files:**
- Modify: `src\Luban.DataLoader.Builtin\DataVisitors\JsonDataCreator.cs:152-160`

**步骤 1: 修改字段缺失时的处理逻辑**

找到第 152-160 行：

```csharp
else if (f.CType.IsNullable)
{
    fields.Add(null);
}
else
{
    throw new Exception($"结构:'{implBean.FullName}' 字段:'{f.CurrentVariantNameWithFieldNameOrOrigin}' 缺失");
}
```

替换为：

```csharp
else if (f.CType.IsNullable)
{
    fields.Add(null);
}
else
{
    fields.Add(DefaultDataCreator.Ins.Accept(f.CType));
}
```

**步骤 2: 编译验证**

```bash
cd E:\Projects\luban\src && dotnet build Luban.sln
```

**步骤 3: 提交**

```bash
git commit -am "feat: JsonDataCreator 支持缺失字段使用默认值"
```

---

## Task 5: 验证 `Accept(TBean, ExcelStream)` 中间空位处理

**Files:**
- Modify: `src\Luban.DataLoader.Builtin\DataVisitors\ExcelStreamDataCreator.cs:286-334`

**步骤 1: 验证中间空位场景**

中间空位（如 `"0,,hello"`）的处理逻辑已经在各个类型的 `Accept` 方法中通过 `CheckDefault` 处理。但需要注意：当 `ExcelStream` 的 `Read()` 返回 null 时，各类型需要能正确处理。

检查 `TInt`, `TString`, `TEnum` 等类型的 `Accept` 方法是否对空值有处理。当前代码中：
- `TInt` 等数值类型：`CheckDefault(x)` 分支已有，返回 `DInt.Default`
- `TString`：`ParseString` 方法处理 null 情况
- `TEnum`：`CheckDefault(x)` 分支已有处理

这些已有逻辑不需要改。确认这一点后继续。

---

## Task 6: 端到端验证

**步骤 1: 构建 Release 版本**

```bash
cd E:\Projects\luban\src && dotnet build Luban.sln -c Release
```

**步骤 2: 检查是否可以使用现有测试表验证**

**步骤 3: 如果有测试配置表，运行一次导表验证**

**步骤 4: 提交最终 commit**

---

## 风险和注意事项

1. **`Accept(TBean, ExcelStream)` 中的多态 bean 逻辑不受影响** — `DefaultDataCreator` 对多态 bean 抛异常，但只有缺失字段路径会走到 `DefaultDataCreator`。多态 bean 的类型标识读取（`x.Read()`）发生在 `CreateBeanFields` 调用之前，不受影响。

2. **`Accept(TBean, RowColumnSheet, TitleRow)` 中的多态/可空 bean 逻辑不受影响** — 同样，类型标识检查在 `CreateBeanFields` 之前执行。

3. **`NotDefaultValueValidator` 仍然会报告默认值** — 这是预期行为。如果用户希望默认值不触发 validator 告警，需要另外配置。
