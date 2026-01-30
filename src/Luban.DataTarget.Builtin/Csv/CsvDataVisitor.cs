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

using System.Text;
using Luban.Datas;
using Luban.DataVisitors;

namespace Luban.DataExporter.Builtin.Csv;

public class CsvDataVisitor : ToLiteralVisitorBase
{
    public static CsvDataVisitor Ins { get; } = new();

    public override string Accept(DBool type)
    {
        return type.Value ? "true" : "false";
    }

    public override string Accept(DString type)
    {
        return type.Value ?? "";
    }

    public override string Accept(DDateTime type)
    {
        return type.ToFormatString();
    }

    public override string Accept(DBean type)
    {
        // 对于复杂对象，转换为JSON格式字符串
        var sb = new StringBuilder();
        sb.Append('{');
        
        int index = 0;
        foreach (var field in type.Fields)
        {
            if (index > 0) sb.Append(';');
            
            var defField = type.ImplType.HierarchyFields[index++];
            sb.Append(defField.Name).Append(':');
            
            if (field != null)
            {
                sb.Append(field.Apply(this));
            }
            else
            {
                sb.Append("null");
            }
        }
        
        sb.Append('}');
        return sb.ToString();
    }

    public override string Accept(DArray type)
    {
        return FormatCollection(type.Datas);
    }

    public override string Accept(DList type)
    {
        return FormatCollection(type.Datas);
    }

    public override string Accept(DSet type)
    {
        return FormatCollection(type.Datas);
    }

    public override string Accept(DMap type)
    {
        var sb = new StringBuilder();
        sb.Append('{');
        
        bool first = true;
        foreach (var kvp in type.DataMap)
        {
            if (!first) sb.Append(';');
            first = false;
            
            sb.Append(kvp.Key.Apply(this));
            sb.Append(':');
            sb.Append(kvp.Value.Apply(this));
        }
        
        sb.Append('}');
        return sb.ToString();
    }

    private string FormatCollection(List<DType> items)
    {
        if (items == null || items.Count == 0)
        {
            return "[]";
        }

        var sb = new StringBuilder();
        sb.Append('[');
        
        for (int i = 0; i < items.Count; i++)
        {
            if (i > 0) sb.Append(';');
            sb.Append(items[i].Apply(this));
        }
        
        sb.Append(']');
        return sb.ToString();
    }
}
