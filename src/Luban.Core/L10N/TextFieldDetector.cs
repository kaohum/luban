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

using System.Collections.Generic;
using Luban.Defs;
using Luban.Types;

namespace Luban.L10N
{
    /// <summary>
    /// 递归检测 bean 集合(含父类/子类/容器/内嵌 bean,带环保护)中是否存在 text 标签的 string 字段。
    /// 遍历原则对齐 StructureSignature.AppendBean/AppendType:全字段、带环保护。
    /// 供各代码目标的 indexMode 支持检测(fail-fast/WARN)共用;indexMode 是否开启由调用方判断。
    /// </summary>
    public static class TextFieldDetector
    {
        public static bool HasAnyTextField(IEnumerable<DefBean> beans)
        {
            var visited = new HashSet<DefBean>();
            foreach (var bean in beans)
            {
                if (BeanContainsTextField(bean, visited))
                {
                    return true;
                }
            }
            return false;
        }

        private static bool BeanContainsTextField(DefBean bean, HashSet<DefBean> visited)
        {
            if (!visited.Add(bean))
            {
                return false; // 环保护
            }
            foreach (var field in bean.Fields)
            {
                if (TypeContainsTextField(field.CType, visited))
                {
                    return true;
                }
            }
            if (bean.ParentDefType != null && BeanContainsTextField((DefBean)bean.ParentDefType, visited))
            {
                return true;
            }
            if (bean.Children != null)
            {
                foreach (var child in bean.Children)
                {
                    if (BeanContainsTextField((DefBean)child, visited))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        private static bool TypeContainsTextField(TType type, HashSet<DefBean> visited)
        {
            switch (type)
            {
                case TString s:
                    return s.HasTag("text");
                case TBean b:
                    return BeanContainsTextField(b.DefBean, visited);
                case TArray a:
                    return TypeContainsTextField(a.ElementType, visited);
                case TList l:
                    return TypeContainsTextField(l.ElementType, visited);
                case TSet set:
                    return TypeContainsTextField(set.ElementType, visited);
                case TMap m:
                    return TypeContainsTextField(m.KeyType, visited) || TypeContainsTextField(m.ValueType, visited);
                default:
                    return false;
            }
        }
    }
}
