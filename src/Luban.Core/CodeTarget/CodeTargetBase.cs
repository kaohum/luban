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
using System.Reflection;
using System.Text;
using Luban.CodeFormat;
using Luban.CodeFormat.CodeStyles;
using Luban.Defs;
using Luban.Utils;
using NLog;

namespace Luban.CodeTarget;

public abstract class CodeTargetBase : ICodeTarget
{
    private static readonly Logger s_logger = LogManager.GetCurrentClassLogger();

    public const string FamilyPrefix = "codeTarget";

    // l10n indexMode(text 字段转 int)已适配的代码目标;其余目标 text 字段输出 string 声明(数据是 int),
    // ValidateDefinition 阶段统一 WARN(spec §4.7)。cs 系 json 目标由各自覆写更早 fail-fast。
    private static readonly HashSet<string> s_l10nTextIndexSupportedTargets = new(StringComparer.Ordinal)
    {
        "cs-bin",
        "java-json",
        "cs-l10n-language",
    };

    public virtual Encoding FileEncoding
    {
        get
        {
            string encoding = EnvManager.Current.GetOptionOrDefault(Name, BuiltinOptionNames.FileEncoding, true, "");
            return string.IsNullOrEmpty(encoding) ? Encoding.UTF8 : System.Text.Encoding.GetEncoding(encoding);
        }
    }

    protected virtual string GetFileNameWithoutExtByTypeName(string name)
    {
        return name;
    }

    protected virtual ICodeStyle DefaultCodeStyle => CodeFormatManager.Ins.NoneCodeStyle;

    protected virtual ICodeStyle CodeStyle => _codeStyle ??= CreateConfigurableCodeStyle();

    private ICodeStyle _codeStyle;

    private ICodeStyle CreateConfigurableCodeStyle()
    {
        var baseStyle = GenerationContext.Current.GetCodeStyle(Name) ?? DefaultCodeStyle;

        var env = EnvManager.Current;
        string namingKey = BuiltinOptionNames.NamingConvention;
        return new OverlayCodeStyle(baseStyle,
            env.GetOptionOrDefault($"{namingKey}.{Name}", "namespace", true, ""),
            env.GetOptionOrDefault($"{namingKey}.{Name}", "type", true, ""),
            env.GetOptionOrDefault($"{namingKey}.{Name}", "method", true, ""),
            env.GetOptionOrDefault($"{namingKey}.{Name}", "property", true, ""),
            env.GetOptionOrDefault($"{namingKey}.{Name}", "field", true, ""),
            env.GetOptionOrDefault($"{namingKey}.{Name}", "enumItem", true, "")
            );
    }

    protected abstract IReadOnlySet<string> PreservedKeyWords { get; }

    protected bool IsPreserveKeyWords(string name)
    {
        return PreservedKeyWords.Contains(name);
    }

    protected virtual bool IsValidateName(string name, NameLocation location)
    {
        return !string.IsNullOrEmpty(name);
    }

    protected virtual void ValidationTypeNames(GenerationContext ctx)
    {
        foreach (var table in ctx.ExportTables)
        {
            if (IsPreserveKeyWords(table.Name))
            {
                throw new Exception($"table name {table.FullName} is preserved keyword");
            }
            if (!IsValidateName(table.Name, NameLocation.TableName))
            {
                throw new Exception($"table name {table.FullName} is invalid");
            }
        }

        foreach (var bean in ctx.ExportBeans)
        {
            if (IsPreserveKeyWords(bean.Name))
            {
                throw new Exception($"bean name {bean.FullName} is preserved keyword");
            }
            if (!IsValidateName(bean.Name, NameLocation.BeanName))
            {
                throw new Exception($"bean name {bean.FullName}  is invalid");
            }
            foreach (var field in bean.Fields)
            {
                if (IsPreserveKeyWords(field.Name))
                {
                    throw new Exception($"the name of field {bean.FullName}::{field.Name} is preserved keyword");
                }
                if (!IsValidateName(field.Name, NameLocation.BeanFieldName))
                {
                    throw new Exception($"the name of field {bean.FullName}::{field.Name} is invalid");
                }
            }
        }

        foreach (var @enum in ctx.ExportEnums)
        {
            if (IsPreserveKeyWords(@enum.Name))
            {
                throw new Exception($"enum name {@enum.FullName} is preserved keyword");
            }
            if (!IsValidateName(@enum.Name, NameLocation.EnumName))
            {
                throw new Exception($"enum name {@enum.FullName}  is invalid");
            }
            foreach (var item in @enum.Items)
            {
                if (IsPreserveKeyWords(item.Name))
                {
                    throw new Exception($"the name of enum item '{@enum.FullName}::{item.Name}' is preserved keyword");
                }
                if (!IsValidateName(item.Name, NameLocation.EnumItemName))
                {
                    throw new Exception($"the name of enum item '{@enum.FullName}::{item.Name}' is invalid");
                }
            }
        }
    }

    public virtual void ValidateDefinition(GenerationContext ctx)
    {
        ValidationTypeNames(ctx);
        WarnUnsupportedL10NTextIndex(ctx);
    }

    /// <summary>
    /// l10n indexMode 开启且导出 bean 含 text 字段时,未适配目标会生成"string 声明 + int 数据"的不一致代码。
    /// 此处集中 WARN(spec §4.7):输出 string 并提示改用 cs-bin/java-json,不阻断生成。
    /// 扫描结果在 GenerationContext 上缓存(HasTextTaggedExportField),多代码目标只算一次。
    /// </summary>
    private void WarnUnsupportedL10NTextIndex(GenerationContext ctx)
    {
        if (!ctx.L10NTextIndexEnabled || s_l10nTextIndexSupportedTargets.Contains(Name) || !ctx.HasTextTaggedExportField)
        {
            return;
        }
        s_logger.Warn($"[{Name}] l10n indexMode(text 字段转 int)未适配该代码目标:text 字段将输出 string 声明但数据是 int 下标,请改用 cs-bin/java-json 或关闭 indexMode");
    }

    /// <summary>
    /// 解析 {targetName}.excludeTables 选项（逗号/分号分隔表名，OrdinalIgnoreCase）。
    /// 未配置或解析后为空集合时返回 null（调用方据此零行为）。
    /// </summary>
    protected static HashSet<string> ParseExcludeTables(string targetName)
    {
        string value = EnvManager.Current.GetOptionOrDefault(targetName, BuiltinOptionNames.ExcludeTables, true, "");
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var names = new HashSet<string>(
            value.Split(',', ';').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)),
            StringComparer.OrdinalIgnoreCase);
        return names.Count > 0 ? names : null;
    }

    /// <summary>表是否命中排除名单（按 Name 与 FullName，OrdinalIgnoreCase）。</summary>
    protected static bool IsExcluded(DefTable table, HashSet<string> excludeNames)
    {
        return excludeNames != null && excludeNames.Count > 0
            && (excludeNames.Contains(table.Name) || excludeNames.Contains(table.FullName));
    }

    /// <summary>
    /// 本代码目标参与表级代码生成的表集合。默认全量 ExportTables；
    /// 需要按目标排除表级代码的后代（如 cs-bin）覆写此方法过滤。
    /// 同时影响表类文件、Tables manager（GenerateTables 的入参）等所有表级迭代点。
    /// </summary>
    protected virtual List<DefTable> FilterTablesForCode(GenerationContext ctx)
    {
        return ctx.ExportTables;
    }

    /// <summary>
    /// 表是否被本代码目标排除出表级代码生成（excludeTables 选项）。
    /// 供模板经 is_excluded_table 查询：tables_by_tag 这类数据层表集合不经过
    /// FilterTablesForCode，模板若以其生成表名引用（如 TableNamesWith 数组），
    /// 需用它过滤掉被排除表，避免引用不存在的表级符号。
    /// </summary>
    public virtual bool IsTableExcluded(DefTable table)
    {
        return false;
    }

    public virtual void Handle(GenerationContext ctx, OutputFileManifest manifest)
    {
        var exportTables = FilterTablesForCode(ctx);
        var tasks = new List<Task<OutputFile>>();
        tasks.Add(Task.Run(() =>
        {
            var writer = new CodeWriter();
            GenerateTables(ctx, exportTables, writer);
            return CreateOutputFile($"{GetFileNameWithoutExtByTypeName(ctx.Target.Manager)}.{FileSuffixName}", writer.ToResult(FileHeader));
        }));

        foreach (var table in exportTables)
        {
            tasks.Add(Task.Run(() =>
            {
                var writer = new CodeWriter();
                GenerateTable(ctx, table, writer);
                return CreateOutputFile($"{GetFileNameWithoutExtByTypeName(table.FullName)}.{FileSuffixName}", writer.ToResult(FileHeader));
            }));
        }

        foreach (var bean in ctx.ExportBeans)
        {
            tasks.Add(Task.Run(() =>
            {
                var writer = new CodeWriter();
                GenerateBean(ctx, bean, writer);
                return CreateOutputFile($"{GetFileNameWithoutExtByTypeName(bean.FullName)}.{FileSuffixName}", writer.ToResult(FileHeader));
            }));
        }

        foreach (var @enum in ctx.ExportEnums)
        {
            tasks.Add(Task.Run(() =>
            {
                var writer = new CodeWriter();
                GenerateEnum(ctx, @enum, writer);
                return CreateOutputFile($"{GetFileNameWithoutExtByTypeName(@enum.FullName)}.{FileSuffixName}", writer.ToResult(FileHeader));
            }));
        }

        Task.WaitAll(tasks.ToArray());
        foreach (var task in tasks)
        {
            manifest.AddFile(task.Result);
        }
    }

    public string Name => GetType().GetCustomAttribute<CodeTargetAttribute>().Name;

    public abstract string FileHeader { get; }

    protected abstract string FileSuffixName { get; }

    public virtual string GetPathFromFullName(string fullName)
    {
        return fullName.Replace('.', '/') + "." + FileSuffixName;
    }

    private static string GetLineEnding()
    {
        string endings = EnvManager.Current.GetOptionOrDefault("code", BuiltinOptionNames.LineEnding, true, "").ToLowerInvariant();
        return StringUtil.GetLineEnding(endings);
    }

    protected OutputFile CreateOutputFile(string path, string content)
    {
        string finalContent = content.ReplaceLineEndings(GetLineEnding());
        return new OutputFile() { File = path, Content = finalContent, Encoding = FileEncoding };
    }

    public abstract void GenerateTables(GenerationContext ctx, List<DefTable> tables, CodeWriter writer);
    public abstract void GenerateTable(GenerationContext ctx, DefTable table, CodeWriter writer);
    public abstract void GenerateBean(GenerationContext ctx, DefBean bean, CodeWriter writer);
    public abstract void GenerateEnum(GenerationContext ctx, DefEnum @enum, CodeWriter writer);
}
