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

using Luban.CSharp.TypeVisitors;
using Luban.Defs;
using Luban.Types;
using Scriban.Runtime;

namespace Luban.CSharp.TemplateExtensions;

public class CsharpBinTemplateExtension : ScriptObject
{
    public static string Deserialize(string bufName, string fieldName, TType type)
    {
        return type.Apply(BinaryDeserializeVisitor.Ins, bufName, fieldName);
    }

    /// <summary>bin-row v3 二阶段：取某表某字段的列编码方案（导出期 plan sidecar）。</summary>
    public static BinRowPlanField BinPlanField(DefTable table, string fieldName)
    {
        var plan = BinRowPlanStore.Get(table);
        if (!plan.FieldsByName.TryGetValue(fieldName, out var f))
        {
            throw new System.Exception($"[cs-bin] plan 缺列: {table.FullName}.{fieldName}");
        }
        return f;
    }

    /// <summary>行步长（生成期常量来源）。</summary>
    public static int BinPlanStride(DefTable table) => BinRowPlanStore.Get(table).Stride;

    /// <summary>plan 段 FNV1a64 哈希（C# ulong 字面量文本，供加载期与文件头 plan 段比对 fail-loud）。</summary>
    public static string BinPlanHash(DefTable table) => BinRowPlanStore.Get(table).Hash.ToString() + "UL";
}
