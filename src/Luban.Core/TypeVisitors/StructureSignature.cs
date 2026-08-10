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
using Luban.Defs;
using Luban.Types;
using Luban.Utils;

namespace Luban.TypeVisitors;

/// <summary>
/// 计算表的结构签名 SignatureId（大写 hex MD5）。
/// 全字段、不调 NeedExport -> c/s 同值（与 BinaryChecksumVisitor 同原则）。
/// 与 GetTypeId 无关（GetTypeId 是名字 hash、多态判别；本类不碰它）。
/// 遍历逻辑对齐 DeepCompareTypeDefine，但输出 hash 而非 bool。
/// </summary>
public static class StructureSignature
{
    public static string ComputeForTable(DefTable table)
    {
        var sb = new StringBuilder();
        var visited = new HashSet<DefBean>();
        sb.Append("T|").Append(table.FullName).Append('|').Append(table.Mode).Append('|').Append(table.Index ?? "").Append('\n');
        if (table.ValueTType is TBean tb)
        {
            AppendBean(sb, tb.DefBean, visited);
        }
        return FileUtil.CalcMD5(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    private static void AppendBean(StringBuilder sb, DefBean bean, HashSet<DefBean> visited)
    {
        if (!visited.Add(bean)) return; // 环保护
        sb.Append("B|").Append(bean.FullName).Append('|').Append(bean.Parent ?? "")
          .Append('|').Append(bean.Alias ?? "").Append('|').Append(bean.IsMultiRow)
          .Append('|').Append(bean.Sep ?? "").Append('\n');
        sb.Append("FC|").Append(bean.Fields.Count).Append('\n');
        for (int i = 0; i < bean.Fields.Count; i++)
        {
            var f = bean.Fields[i];
            sb.Append("F|").Append(i).Append('|').Append(f.Name).Append('|').Append(f.CType.IsNullable).Append('|');
            AppendType(sb, f.CType, visited);
            sb.Append('\n');
        }
        if (bean.ParentDefType != null) AppendBean(sb, (DefBean)bean.ParentDefType, visited);
        if (bean.Children != null)
        {
            sb.Append("CC|").Append(bean.Children.Count).Append('\n');
            foreach (var c in bean.Children) AppendBean(sb, (DefBean)c, visited);
        }
    }

    private static void AppendType(StringBuilder sb, TType t, HashSet<DefBean> visited)
    {
        switch (t)
        {
            case TBean b: sb.Append("Bean:"); AppendBean(sb, b.DefBean, visited); break;
            case TEnum e:
                sb.Append("Enum:").Append(e.DefEnum.FullName).Append('|').Append(e.DefEnum.IsFlags)
                  .Append('|').Append(e.DefEnum.IsUniqueItemId).Append('|').Append(e.DefEnum.Items.Count);
                foreach (var it in e.DefEnum.Items) sb.Append('|').Append(it.Name).Append('=').Append(it.Value).Append(':').Append(it.Alias ?? "");
                break;
            case TArray a: sb.Append("Array:"); AppendType(sb, a.ElementType, visited); break;
            case TList l: sb.Append("List:"); AppendType(sb, l.ElementType, visited); break;
            case TSet s: sb.Append("Set:"); AppendType(sb, s.ElementType, visited); break;
            case TMap m: sb.Append("Map:"); AppendType(sb, m.KeyType, visited); AppendType(sb, m.ValueType, visited); break;
            default: sb.Append("P:").Append(t.GetType().Name); break; // TBool/TInt/TString/TDateTime/TDay...
        }
    }
}
