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

using Luban.CodeTarget;
using Scriban;
using Scriban.Runtime;

namespace Luban.CSharp.CodeTarget;

// 生成本地化 key 映射文件（依赖 l10n 数据已加载）
[CodeTarget("cs-l10n-language")]
public class CsharpL10NLanguageCodeTarget : CsharpCodeTargetBase
{
    public override void Handle(GenerationContext ctx, OutputFileManifest manifest)
    {
        // 确保可访问数据
        ctx.LoadDatas();
        var keys = ctx.GetL10NKeyInfos();

        var template = GetTemplate("language");
        var tplCtx = CreateTemplateContext(template);

        string className = EnvManager.Current.GetOptionOrDefault(Name, "className", true, "LanguageFields");
        string ns = ctx.Target.TopModule;

        var extra = new ScriptObject
        {
            { "__ctx", ctx },
            { "__namespace", ns },
            { "__class_name", className },
            { "__keys", keys },
        };
        tplCtx.PushGlobal(extra);

        var writer = new CodeWriter();
        writer.Write(template.Render(tplCtx));

        manifest.AddFile(CreateOutputFile($"{className}.cs", writer.ToResult(FileHeader)));
    }
}

