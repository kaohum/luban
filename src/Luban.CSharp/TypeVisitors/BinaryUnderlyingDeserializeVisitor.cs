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

using Luban.Types;
using Luban.TypeVisitors;
using Luban.Utils;

namespace Luban.CSharp.TypeVisitors;

public class BinaryUnderlyingDeserializeVisitor : ITypeFuncVisitor<string, string, int, string>
{
    public static BinaryUnderlyingDeserializeVisitor Ins { get; } = new();

    public string Accept(TBool type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadBool();";
    }

    public string Accept(TByte type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadByte();";
    }

    public string Accept(TShort type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadShort();";
    }
    public string Accept(TInt type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadInt();";
    }

    public string Accept(TLong type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadLong();";
    }

    public string Accept(TFloat type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadFloat();";
    }

    public string Accept(TDouble type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = {bufName}.ReadDouble();";
    }

    public string Accept(TEnum type, string bufName, string fieldName, int depth)
    {
        return $"{fieldName} = ({type.Apply(UnderlyingDeclaringTypeNameVisitor.Ins)}){bufName}.ReadInt();";
    }

    public string Accept(TString type, string bufName, string fieldName, int depth)
    {
        if (L10NTextIndexTypeUtil.IsTextIndex(type))
        {
            return $"{fieldName} = {bufName}.ReadInt();";
        }
        return $"{fieldName} = {bufName}.ReadStringIndex();";
    }

    public string Accept(TDateTime type, string bufName, string fieldName, int depth)
    {
        string src = $"{bufName}.ReadLong()";
        return $"{fieldName} = {src};";
    }

    public string Accept(TDay type, string bufName, string fieldName, int depth)
    {
        string src = $"{bufName}.ReadLong()";
        return $"{fieldName} = {src};";
    }

    public string Accept(THour type, string bufName, string fieldName, int depth)
    {
        string src = $"{bufName}.ReadLong()";
        return $"{fieldName} = {src};";
    }

    public string Accept(TMinute type, string bufName, string fieldName, int depth)
    {
        string src = $"{bufName}.ReadLong()";
        return $"{fieldName} = {src};";
    }

    public string Accept(TSecond type, string bufName, string fieldName, int depth)
    {
        string src = $"{bufName}.ReadLong()";
        return $"{fieldName} = {src};";
    }

    public string Accept(TMillisecond type, string bufName, string fieldName, int depth)
    {
        string src = $"{bufName}.ReadLong()";
        return $"{fieldName} = {src};";
    }

    public string Accept(TBean type, string bufName, string fieldName, int depth)
    {
        string src = $"{CSharpUtil.GetFullNameWithGlobalQualifier(type.DefBean)}.Deserialize{type.DefBean.Name}({bufName})";
        string constructor = type.DefBean.TypeConstructorWithTypeMapper();
        return $"{fieldName} = {(string.IsNullOrEmpty(constructor) ? src : $"{constructor}({src})")};";
    }

    public static string CreateNewArrayWithSize(TArray type, string n)
    {
        string typeStr = $"{type.ElementType.Apply(DeclaringTypeNameVisitor.Ins)}[{n}]";
        if (type.Dimension > 1)
        {
            typeStr = $"{type.FinalElementType.Apply(UnderlyingDeclaringTypeNameVisitor.Ins)}[{n}]";
            for (int i = 0; i < type.Dimension - 1; i++)
            {
                typeStr += "[]";
            }
        }
        return typeStr;
    }

    public string Accept(TArray type, string bufName, string fieldName, int depth)
    {
        // 原语元素一维数组走 ByteBuf.ReadXxxArray 批量读取:解码循环落在 AOT 运行时侧,
        // 热更域解释器每个数组只付一次调用开销——逐元素解释循环是热更配置解表的 dominate 耗时。
        // 不适用批量(维持逐元素循环):多维数组、可空元素、byte/double(游戏流式读取器未 override,
        // 流上会读错数据)、枚举(字段类型不匹配)、文本索引串(元素是 ReadInt 而非字符串索引)。
        if (type.Dimension == 1 && !type.ElementType.IsNullable)
        {
            string bulkMethod = TryGetBulkReadMethodName(type.ElementType);
            if (bulkMethod != null)
            {
                return $"{fieldName} = {bufName}.{bulkMethod}();";
            }
        }
        string n = $"__n{depth}";
        string e = $"__e{depth}";
        string index = $"__index{depth}";
        string typeStr = CreateNewArrayWithSize(type, n);
        return $"{{int {n} = {bufName}.ReadSize(); {fieldName} = new {typeStr};for(var {index} = 0 ; {index} < {n} ; {index}++) {{ {type.ElementType.Apply(DeclaringTypeNameVisitor.Ins)} {e};{type.ElementType.Apply(this, bufName, $"{e}", depth + 1)} {fieldName}[{index}] = {e};}}}}";
    }

    /// <summary>
    /// 原语元素类型对应的 ByteBuf 批量读取方法名；无批量方法（需逐元素循环）返回 null。
    /// 仅限游戏流式读取器已 override 的元素读取（bool/short/int/long/float/字符串索引）。
    /// </summary>
    private static string TryGetBulkReadMethodName(TType elementType)
    {
        switch (elementType)
        {
            case TInt: return "ReadIntArray";
            case TLong: return "ReadLongArray";
            case TFloat: return "ReadFloatArray";
            case TBool: return "ReadBoolArray";
            case TShort: return "ReadShortArray";
            case TString ts when !L10NTextIndexTypeUtil.IsTextIndex(ts): return "ReadStringIndexArray";
            default: return null;
        }
    }

    public string Accept(TList type, string bufName, string fieldName, int depth)
    {
        string n = $"n{depth}";
        string e = $"_e{depth}";
        string i = $"i{depth}";
        return $"{{int {n} = {bufName}.ReadSize(); {fieldName} = new {type.Apply(DeclaringTypeNameVisitor.Ins)}({n});for(var {i} = 0 ; {i} < {n} ; {i}++) {{ {type.ElementType.Apply(DeclaringTypeNameVisitor.Ins)} {e};  {type.ElementType.Apply(this, bufName, $"{e}", depth + 1)} {fieldName}.Add({e});}}}}";
    }

    public string Accept(TSet type, string bufName, string fieldName, int depth)
    {
        string n = $"n{depth}";
        string e = $"_e{depth}";
        string i = $"i{depth}";
        return $"{{int {n} = {bufName}.ReadSize(); {fieldName} = new {type.Apply(DeclaringTypeNameVisitor.Ins)}(/*{n} * 3 / 2*/);for(var {i} = 0 ; {i} < {n} ; {i}++) {{ {type.ElementType.Apply(DeclaringTypeNameVisitor.Ins)} {e};  {type.ElementType.Apply(this, bufName, $"{e}", +1)} {fieldName}.Add({e});}}}}";
    }

    public string Accept(TMap type, string bufName, string fieldName, int depth)
    {
        string n = $"n{depth}";
        string k = $"_k{depth}";
        string v = $"_v{depth}";
        string i = $"i{depth}";
        return $"{{int {n} = {bufName}.ReadSize(); {fieldName} = new {type.Apply(DeclaringTypeNameVisitor.Ins)}({n} * 3 / 2);for(var {i} = 0 ; {i} < {n} ; {i}++) {{ {type.KeyType.Apply(DeclaringTypeNameVisitor.Ins)} {k};  {type.KeyType.Apply(this, bufName, k, depth + 1)} {type.ValueType.Apply(DeclaringTypeNameVisitor.Ins)} {v};  {type.ValueType.Apply(this, bufName, v, depth + 1)}     {fieldName}.Add({k}, {v});}}}}";
    }
}
