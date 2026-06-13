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
        var keys = GetCodeL10NKeys(ctx);
        var keyType = keys.Item2;
        if (keyType == null)
        {
            keyType = typeof(int);
        }
        
        var template = GetTemplate("language");
        var tplCtx = CreateTemplateContext(template);

        string className = EnvManager.Current.GetOptionOrDefault(Name, "className", true, "LanguageFields");
        string ns = ctx.Target.TopModule;

        var extra = new ScriptObject
        {
            { "__ctx", ctx },
            { "__namespace", ns },
            { "__class_name", className },
            { "__keys", keys.Item1 },
            { "__key_type", keyType.Name },
        };
        tplCtx.PushGlobal(extra);

        var writer = new CodeWriter();
        writer.Write(template.Render(tplCtx));

        manifest.AddFile(CreateOutputFile($"{className}.cs", writer.ToResult(FileHeader)));
    }

    /// <summary>
    /// 解析 cs-l10n-language.keyTable 配置：
    /// - 配置时：只从指定的“代码引用语言表”枚举 key（静态字段只覆盖该表）。
    /// - 未配置时：枚举全部导出表，保持原行为。
    /// </summary>
    private (IReadOnlyList<L10NKeyInfo>, System.Type) GetCodeL10NKeys(GenerationContext ctx)
    {
        string keyTable = EnvManager.Current.GetOptionOrDefault(Name, "keyTable", false, null);
        if (string.IsNullOrWhiteSpace(keyTable))
        {
            return ctx.GetL10NKeyInfos();
        }

        var matched = ctx.ExportTables.Where(t =>
            string.Equals(t.Name, keyTable, StringComparison.Ordinal) ||
            string.Equals(t.FullName, keyTable, StringComparison.Ordinal) ||
            t.FullName.EndsWith("." + keyTable, StringComparison.Ordinal)).ToList();

        if (matched.Count == 0)
        {
            var available = string.Join(", ", ctx.ExportTables.Select(t => t.FullName));
            throw new Exception(
                $"[cs-l10n-language] keyTable='{keyTable}' 未匹配到任何导出表。" +
                $"请检查 language schema 中的 table 定义或 lang.conf 中 cs-l10n-language.keyTable 配置。当前可用表：{available}");
        }

        return ctx.GetL10NKeyInfos(matched);
    }
}

