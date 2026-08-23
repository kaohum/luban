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

using System;
using System.Collections.Generic;
using System.Linq;
using Luban.CodeTarget;
using Luban.Defs;
using Luban.L10N;
using Luban.Utils;
using NLog;
using Scriban;
using Scriban.Runtime;

namespace Luban.CSharp.CodeTarget;

// 生成本地化 key 访问器映射文件（依赖 l10n 数据已加载）。
// v2（spec 2026-08-22 D7）：单形态——只出访问器 `public static string {name} => Get({id});`，
// id 烘焙自语言表 id 列，访问器名取 name 列（无 name 列的空间退化为 id/key 派生）；
// Get(int)/数据容器（dataMapRef 字典）移至各 space 的手写 partial（客户端 Tasks 7/8 提供，
// server space 手写 partial 已有 Dictionary<int,string> + Get(int)）。
[CodeTarget("cs-l10n-language")]
public class CsharpL10NLanguageCodeTarget : CsharpCodeTargetBase
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    public override void Handle(GenerationContext ctx, OutputFileManifest manifest)
    {
        // 确保可访问数据（LoadDatas 末尾已为 indexMode space 构建 id 注册表）
        ctx.LoadDatas();

        // 多 space：跟随数据管线的 l10n.spaces（唯一事实源），每 space 输出一个类；
        // className/keyFlag/keyTable/outputDir 可按 cs-l10n-language.space.{name}.* 覆盖默认值；
        // genAccessors=false（AF-3/AF-16）的 space 不产出任何访问器文件（数据导出/sidecar 不受影响）。
        if (ctx.L10NSpaces.Count > 0)
        {
            foreach (var space in ctx.L10NSpaces)
            {
                if (!space.GenAccessors)
                {
                    s_logger.Debug("[cs-l10n-language] space '{space}' 配置 genAccessors=false,跳过访问器生成", space.Name);
                    continue;
                }
                RenderSpace(ctx, manifest, space,
                    EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.className", false, DefaultClassName(space)),
                    EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.keyFlag", false, null),
                    EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.keyTable", false, null),
                    EnvManager.Current.GetOptionOrDefault(Name, $"space.{space.Name}.outputDir", false, null));
            }
            return;
        }

        // 旧单值选项路径（未配置 spaces）：单次渲染。
        // 该路径无 id 注册表（无 desync 守卫），Get(int) 的手写 partial 配对关系不由 spaces 配置保证，
        // 属过渡形态；string key（无 int id）时访问器烘焙 -1 恒空串。正式配置请迁移 l10n.spaces。
        if (ctx.L10NLanguages.Count > 0)
        {
            s_logger.Warn("[cs-l10n-language] 未配置 l10n.spaces,静态访问器走旧单值选项过渡形态(无 id 注册表守卫);请迁移 spaces 配置");
        }
        var keys = GetCodeL10NKeys(ctx);
        string className = EnvManager.Current.GetOptionOrDefault(Name, "className", true, "LanguageFields");
        GuardKeyNameUniqueness(keys, className);
        RenderLanguageClass(ctx, manifest, keys, className, null);
    }

    /// <summary>默认类名：main→LanguageConfig，其他→Language{Name}Config（首字母大写）。</summary>
    private static string DefaultClassName(L10NSpace space)
    {
        return space.Name == "main"
            ? "LanguageConfig"
            : $"Language{TypeUtil.UpperCaseFirstChar(space.Name)}Config";
    }

    private void RenderSpace(GenerationContext ctx, OutputFileManifest manifest, L10NSpace space,
        string className, string keyFlag, string keyTable, string outputDir)
    {
        // 表集合：keyTable 选项 > space.Tables 名单（与 id 注册表同源）
        List<DefTable> tables;
        if (!string.IsNullOrWhiteSpace(keyTable))
        {
            tables = ctx.ExportTables.Where(t =>
                string.Equals(t.Name, keyTable, StringComparison.Ordinal) ||
                string.Equals(t.FullName, keyTable, StringComparison.Ordinal) ||
                t.FullName.EndsWith("." + keyTable, StringComparison.Ordinal)).ToList();
            if (tables.Count == 0)
            {
                var available = string.Join(", ", ctx.ExportTables.Select(t => t.FullName));
                throw new Exception(
                    $"[cs-l10n-language] space '{space.Name}' 的 keyTable='{keyTable}' 未匹配到任何导出表。" +
                    $"请检查 language schema 中的 table 定义或 cs-l10n-language.space.{space.Name}.keyTable 配置。当前可用表：{available}");
            }
        }
        else
        {
            tables = L10NKeyIndexBuilder.MatchTables(ctx, space);
        }

        // id/name/desc 字段与语言名单用 space 自己的配置；flag 过滤沿用 GetL10NKeyInfos(tables, flag)
        var keys = ctx.GetL10NKeyInfos(tables,
            string.IsNullOrWhiteSpace(keyFlag) ? null : keyFlag,
            space.Languages, space.KeyFieldName, space.KeyFieldDesc, space.NameFieldName);

        GuardKeyNameUniqueness(keys, className);
        GuardIdInLiveRegistry(space, keys);

        RenderLanguageClass(ctx, manifest, keys, className, outputDir);
    }

    /// <summary>
    /// desync 守卫（name 维度）：同一 space 内 name 列（访问器名来源）重复，或清洗出的访问器名是 C# 关键字，
    /// 都直接抛错——访问器名是代码引用的语义身份，静默加 _2 后缀会让调用方引用到被改写的名字。
    /// 另守卫伴生 id 常量（AF 增量）：常量名 = 访问器名 + "Id"，若恰与另一 key 的访问器名相同，
    /// 生成类会出现重复成员（CS0101），也直接抛错。
    /// </summary>
    private void GuardKeyNameUniqueness(IReadOnlyList<L10NKeyInfo> keys, string className)
    {
        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var accessorNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var k in keys)
        {
            if (k.Name != null && !seenNames.Add(k.Name))
            {
                throw new Exception(
                    $"[cs-l10n-language] 类 {className} 的 name 列值 '{k.Name}'(id {k.Id}) 在本 space 内重复:" +
                    $"name 是静态访问器名,必须唯一,请修正语言表的 name 列");
            }
            if (IsPreserveKeyWords(k.FieldName))
            {
                throw new Exception(
                    $"[cs-l10n-language] 类 {className} 的访问器名 '{k.FieldName}'(id {k.Id}) 是 C# 关键字:" +
                    $"请修改语言表 name 列(或对应 key/id)后重新生成");
            }
            accessorNames.Add(k.FieldName);
        }

        // 伴生 id 常量名（FieldName + "Id"）不得命中任何访问器名；FieldName 已唯一 => 常量名彼此必唯一
        foreach (var k in keys)
        {
            if (accessorNames.Contains(k.FieldName + "Id"))
            {
                throw new Exception(
                    $"[cs-l10n-language] 类 {className} 的访问器名 '{k.FieldName}Id' 与 key '{k.FieldName}'(id {k.Id})" +
                    $"的伴生 id 常量名 '{k.FieldName}Id' 冲突:生成类会出现重复成员,请修改语言表 name 列(或对应 key/id)后重新生成");
            }
        }
    }

    /// <summary>
    /// desync 守卫（id 维度）：key 行的 id 不在该 space 的活 id 集 = 枚举结果与注册表不同步（疑似 bug）。
    /// 仅 indexMode space 有注册表（LoadDatas 末尾构建）；server 等非 indexMode space 无注册表可查，跳过。
    /// </summary>
    private void GuardIdInLiveRegistry(L10NSpace space, IReadOnlyList<L10NKeyInfo> keys)
    {
        if (space.KeyIndex == null)
        {
            return;
        }
        foreach (var k in keys)
        {
            if (!space.KeyIndex.LiveIds.Contains(k.Id))
            {
                throw new Exception(
                    $"[cs-l10n-language] space '{space.Name}' 的 key '{k.FieldName}'(id {k.Id}) 不在活 id 注册表中" +
                    "(枚举数据与注册表不同步,疑似 bug)");
            }
        }
    }

    /// <summary>渲染 language.sbn 并把 {outputDir?/}{className}.cs 加入 manifest。</summary>
    private void RenderLanguageClass(GenerationContext ctx, OutputFileManifest manifest,
        IReadOnlyList<L10NKeyInfo> keys, string className, string outputDir)
    {
        var template = GetTemplate("language");
        var tplCtx = CreateTemplateContext(template);

        var extra = new ScriptObject
        {
            { "__ctx", ctx },
            { "__namespace", ctx.Target.TopModule },
            { "__class_name", className },
            { "__keys", keys },
        };
        tplCtx.PushGlobal(extra);

        var writer = new CodeWriter();
        writer.Write(template.Render(tplCtx));

        string file = string.IsNullOrWhiteSpace(outputDir)
            ? $"{className}.cs"
            : $"{outputDir.TrimEnd('/', '\\').Replace('\\', '/')}/{className}.cs";
        manifest.AddFile(CreateOutputFile(file, writer.ToResult(FileHeader)));
    }

    /// <summary>
    /// 解析 cs-l10n-language 的 key 过滤配置（未配置 spaces 的旧单值路径）：
    /// - keyTable：只从指定的“代码引用语言表”枚举 key（不配置则枚举全部导出表）。
    /// - keyFlag：按行内 bool 标记字段过滤，仅该字段为 true 的行生成静态字段
    ///   （标记字段只影响代码生成，不进导出的语言 bin）。
    /// 两项可叠加：先按表过滤，再按行过滤。均不配置时保持原行为。
    /// </summary>
    private IReadOnlyList<L10NKeyInfo> GetCodeL10NKeys(GenerationContext ctx)
    {
        string keyTable = EnvManager.Current.GetOptionOrDefault(Name, "keyTable", false, null);
        string keyFlag = EnvManager.Current.GetOptionOrDefault(Name, "keyFlag", false, null);

        if (string.IsNullOrWhiteSpace(keyTable))
        {
            if (string.IsNullOrWhiteSpace(keyFlag))
            {
                return ctx.GetL10NKeyInfos();
            }
            return ctx.GetL10NKeyInfos(ctx.ExportTables, keyFlag);
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

        return ctx.GetL10NKeyInfos(matched, string.IsNullOrWhiteSpace(keyFlag) ? null : keyFlag);
    }
}
