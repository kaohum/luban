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

using Luban.Defs;
using Luban.L10N;
using Luban.Types;

namespace Luban.CSharp.TypeVisitors;

/// <summary>L10N index 模式下,text 标签的 string 字段在 C# 中按 int 处理。</summary>
public static class L10NTextIndexTypeUtil
{
    public static bool IsTextIndex(TType type)
    {
        return GenerationContext.Current.L10NTextIndexEnabled && type is TString && type.HasTag("text");
    }

    /// <summary>
    /// index 模式开启时,指定 bean 集合(含父类/子类/容器/内嵌 bean 递归)中是否存在 text 字段。
    /// 递归遍历逻辑在 Core 的 <see cref="TextFieldDetector"/>(各代码目标共用),此处只叠加 indexMode 开关。
    /// </summary>
    public static bool HasAnyTextField(IEnumerable<DefBean> beans)
    {
        if (!GenerationContext.Current.L10NTextIndexEnabled)
        {
            return false;
        }
        return TextFieldDetector.HasAnyTextField(beans);
    }

    /// <summary>
    /// lan-index 模式暂不支持的代码目标(cs 系 json 目标)在 ValidateDefinition 阶段 fail-fast,
    /// 避免生成"int 声明 + string 读写"的必然编译失败的代码。
    /// </summary>
    public static void EnsureSupportedForIndexMode(string targetName, GenerationContext ctx, IEnumerable<DefBean> beans)
    {
        if (!ctx.L10NTextIndexEnabled)
        {
            return;
        }
        if (HasAnyTextField(beans))
        {
            throw new Exception($"[{targetName}] lan-index 模式(text 字段转 int)暂不支持该目标,请使用 cs-bin/java-json 或关闭 indexMode");
        }
    }
}
