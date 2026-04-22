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
using Luban.Utils;

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
            throw new Exception($"结构:'{originBean.FullName}' 是多态类型，无法创建默认值");
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
        return new DSet(type, new List<DType>());
    }

    public DType Accept(TMap type)
    {
        return new DMap(type, new Dictionary<DType, DType>());
    }
}
