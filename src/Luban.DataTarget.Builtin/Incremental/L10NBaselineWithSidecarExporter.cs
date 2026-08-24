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
using System.IO;
using System.Linq;
using Luban.Checksum;
using Luban.DataTarget;
using Luban.Datas;
using Luban.Defs;
using Luban.Incremental;
using Luban.L10N;
using Luban.Serialization;
using Luban.Types;
using Luban.TypeVisitors;
using Luban.Utils;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// L10N 基准导出 + 写 L10N sidecar + _l10n.checksum.bytes。
/// 包装 L10NBinarySplitDataExporter：先正常按语言拆分导出，再合并所有语言表的 (lang,id)->value，
/// 写 per-语言 id -> MD5(value) 的 sidecar，以及 per-语言整文件 MD5 的 _l10n.checksum.bytes。
/// 全语言共享一个 SignatureId（Language bean 结构签名）。v2：键为显式 int 语言 id。
/// </summary>
[DataExporter("l10n-baseline-with-sidecar")]
public class L10NBaselineWithSidecarExporter : L10NBinarySplitDataExporter
{
    public override void Handle(GenerationContext ctx, IDataTarget dataTarget, OutputFileManifest manifest)
    {
        // 1. 正常 l10n-bin-split 导出（产 language/{lang}/languageconfig.bytes）
        base.Handle(ctx, dataTarget, manifest);

        // 2. 写 L10N sidecar + _l10n.checksum.bytes（失败不阻断导出）
        try
        {
            WriteL10NSidecar(ctx, manifest);
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[l10n-baseline-with-sidecar] write sidecar failed: {e}");
        }
    }

    private void WriteL10NSidecar(GenerationContext ctx, OutputFileManifest manifest)
    {
        var path = EnvManager.Current.GetOptionOrDefault("", BuiltinOptionNames.IncrementalSidecarPath, true, "");
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        // indexMode space 分流：sidecar v2（注册表回写 + 基准快照语义），逐 space 写到各自 sidecar 路径。
        // 未配置 indexMode space 时走下方旧路径，行为完全不变。
        if (ctx.L10NSpaces.Any(s => s.IndexMode))
        {
            foreach (var space in ctx.L10NSpaces.Where(s => s.IndexMode))
            {
                try
                {
                    WriteIndexSpaceSidecar(ctx, space);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine($"[l10n-baseline-with-sidecar] write sidecar for space '{space.Name}' failed: {e}");
                }
            }
            return;
        }

        // 旧路径（未配置 spaces / 全为非 indexMode）：合成单 space（旧单值选项填充，Tables=null 表示全部表），
        // 与 omnibus-baseline 的非 indexMode space 共用同一静态方法，保证旧行为字节级不变。
        var synth = new L10NSpace
        {
            Name = "__legacy__",
            Tables = null,
            Languages = ctx.L10NLanguages.ToList(),
            KeyFieldName = ctx.L10NTextKeyFieldName,
            SidecarPath = path,
        };
        WriteLegacySpaceSidecar(ctx, synth, ctx.GetL10NLangStamps());
    }

    /// <summary>
    /// 非 indexMode space（server 类，int 字典逐表导出）的 L10N sidecar：
    /// Keys = 全语言共享 int id 集合（升序）；Languages[lang].Hashes 与 Keys 对齐；
    /// (ContentHash, Stamp) 由 langStamps 传入（旧路径 = GetL10NLangStamps()，omnibus space 路径 =
    /// GetL10NSpaceLangStamps(space)），与 checksumconfig 语言行共用同一份值。
    /// space.Tables == null 表示全部表（旧路径合成 space 专用，等价合并前行为）。
    /// </summary>
    internal static void WriteLegacySpaceSidecar(GenerationContext ctx, L10NSpace space,
        Dictionary<string, (string ContentHash, long Stamp)> langStamps)
    {
        var path = space.SidecarPath;
        if (string.IsNullOrEmpty(path))
        {
            return;
        }

        var languages = space.Languages;
        var keyFieldName = space.KeyFieldName;
        var tables = space.Tables == null ? null : L10NKeyIndexBuilder.MatchTables(ctx, space);

        // 合并语言表 -> perLang[lang] = id -> value（共享 util，与 checksum 注入一致）
        var perLang = L10NChecksumUtil.BuildPerLanguageMap(ctx, languages, keyFieldName, tables);

        // SignatureId：任取一张 L10N 表的行 bean 结构（全语言共享）
        string sigId = "";
        foreach (var table in tables ?? ctx.Tables)
        {
            if (table.ValueTType is TBean)
            {
                sigId = StructureSignature.ComputeForTable(table);
                break;
            }
        }

        // 共享 int id 集合（所有语言一致；升序保证确定性与幂等——id 在表内，不依赖分配顺序）
        var keySet = new SortedSet<int>();
        foreach (var map in perLang.Values)
        {
            foreach (var k in map.Keys)
            {
                keySet.Add(L10NChecksumUtil.ToIntId(k));
            }
        }
        var keys = keySet.ToList();

        var sidecar = new L10NSidecar { SignatureId = sigId, Keys = keys };
        foreach (var (lang, map) in perLang)
        {
            // id -> value，便于与 Keys 对齐
            var mapInt = new Dictionary<int, string>(map.Count);
            foreach (var kv in map)
            {
                mapInt[L10NChecksumUtil.ToIntId(kv.Key)] = kv.Value ?? "";
            }

            var hashes = new List<string>(keys.Count);
            foreach (var k in keys)
            {
                var v = mapInt.TryGetValue(k, out var val) ? val : "";
                hashes.Add(FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(v)));
            }
            var stampInfo = langStamps.GetValueOrDefault(lang);
            sidecar.Languages[lang] = new LangSidecar
            {
                Hashes = hashes,
                ContentHash = stampInfo.ContentHash,
                Stamp = stampInfo.Stamp,
            };
        }

        // 管线里的语言表（LanguageCode/LanguageText 等）也记 (ContentHash, Stamp)，供下次基准表级戳 gating
        foreach (var table in tables ?? ctx.Tables)
        {
            if (table.Name == Checksum.ChecksumTableBuilder.ChecksumTableName)
            {
                continue;
            }
            if (string.IsNullOrEmpty(table.ContentHash))
            {
                continue;
            }
            sidecar.Tables[table.FullName] = new TableSidecarEntry
            {
                SignatureId = table.SignatureId,
                ContentHash = table.ContentHash,
                Stamp = table.Stamp,
            };
        }
        BaselineSidecarIO.SaveL10N(path, sidecar);
    }

    /// <summary>
    /// indexMode space 的 sidecar v2（int id 语义，基线冻结，仅基准导出写入）：
    /// - Keys = 基准时刻活 id 的升序快照，增量侧按 id 直接比对（见 IncrementalL10NDataExporter.HandleSpace）；
    /// - Languages[lang].Hashes = 与 Keys 对齐的 MD5（缺失 = 空串）；
    ///   (ContentHash, Stamp) 取 ctx.GetL10NSpaceLangStamps(space)（ContentHash = int 字典序列化
    ///   （与导出的 {lang}/{outputFile}.bytes 布局一致）整文件 MD5，Stamp 沿用 gating 语义），
    ///   与该 space 的 checksumconfig 语言行共用同一份计算。
    /// 增量 run 只读本文件（见 IncrementalL10NDataExporter），Keys/Languages 始终是最近一次基准的快照。
    /// </summary>
    internal static void WriteIndexSpaceSidecar(GenerationContext ctx, L10NSpace space)
    {
        if (string.IsNullOrEmpty(space.SidecarPath))
        {
            throw new InvalidOperationException(
                $"[l10n-baseline-with-sidecar] space '{space.Name}' 未配置 l10n.{space.Name}.sidecar，无法写 id 注册表。");
        }
        if (space.KeyIndex == null)
        {
            throw new InvalidOperationException(
                $"[l10n-baseline-with-sidecar] space '{space.Name}' 的 id 注册表未构建（KeyIndex == null）。");
        }

        var tables = L10NKeyIndexBuilder.MatchTables(ctx, space);

        // SignatureId：space 内任取一张语言表的行 bean 结构（全语言共享）
        string sigId = "";
        foreach (var table in tables)
        {
            if (table.ValueTType is TBean)
            {
                sigId = StructureSignature.ComputeForTable(table);
                break;
            }
        }

        // space 名单表合并 -> perLang[lang] = id -> value
        var perLang = L10NChecksumUtil.BuildPerLanguageMap(ctx, space.Languages, space.KeyFieldName, tables);

        // 基准快照 Keys：当前活 id 升序（注册表 = 活 id 集，无墓碑；基线冻结，本文件仅基准导出写入）
        var keys = space.KeyIndex.LiveIds.OrderBy(id => id).ToList();
        var sidecar = new L10NSidecar
        {
            SignatureId = sigId,
            Keys = keys,
        };

        // per-language (ContentHash, Stamp)：共享 ctx 缓存（gating 在 LoadDatas 阶段对该 space 的上次 sidecar 完成），
        // 保证 sidecar 与 {space.OutputDir}/checksumconfig.bytes 的语言行恒为同值。
        var langStamps = ctx.GetL10NSpaceLangStamps(space);

        foreach (var lang in space.Languages)
        {
            var map = perLang.GetValueOrDefault(lang) ?? new Dictionary<object, string>();
            var hashes = new List<string>(keys.Count);
            foreach (var k in keys)
            {
                var v = map.TryGetValue(k, out var val) ? val : "";
                hashes.Add(FileUtil.CalcMD5(System.Text.Encoding.UTF8.GetBytes(v ?? "")));
            }
            var stampInfo = langStamps.GetValueOrDefault(lang);
            sidecar.Languages[lang] = new LangSidecar
            {
                Hashes = hashes,
                ContentHash = stampInfo.ContentHash,
                Stamp = stampInfo.Stamp,
            };
        }

        // space 语言表自身的 (ContentHash, Stamp)，供下次基准表级戳 gating（与旧路径同构）
        foreach (var table in tables)
        {
            if (table.Name == Checksum.ChecksumTableBuilder.ChecksumTableName)
            {
                continue;
            }
            if (string.IsNullOrEmpty(table.ContentHash))
            {
                continue;
            }
            sidecar.Tables[table.FullName] = new TableSidecarEntry
            {
                SignatureId = table.SignatureId,
                ContentHash = table.ContentHash,
                Stamp = table.Stamp,
            };
        }
        BaselineSidecarIO.SaveL10N(space.SidecarPath, sidecar);
    }
}
