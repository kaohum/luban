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

namespace Luban.Incremental;

/// <summary>
/// 基准 sidecar（普通表），per-target。
/// 记录每张表的结构签名、模式、主键、行数及 per-row MD5（按目标 group 过滤字段算）。
/// 供增量导出器做结构 gate + 行级 diff。
/// </summary>
public class BaselineSidecar
{
    public string Target { get; set; } = "";

    public Dictionary<string, TableSidecarEntry> Tables { get; set; } = new();
}

/// <summary>
/// 单张表在基准 sidecar 中的条目。
/// </summary>
public class TableSidecarEntry
{
    public string SignatureId { get; set; } = "";

    public string Mode { get; set; } = "";

    public string PrimaryKeyIndex { get; set; } = "";

    public int RowCount { get; set; }

    /// <summary>
    /// 全量数据内容指纹（MD5，不过 group 过滤）。供下次基准导出做 Stamp gating（内容没变则戳沿用）。
    /// </summary>
    public string ContentHash { get; set; } = "";

    /// <summary>
    /// 内容版本戳（unix 秒）。checksumconfig 对外下发同值；下次基准内容没变则沿用。
    /// </summary>
    public long Stamp { get; set; }

    public Dictionary<string, string> RowHashes { get; set; } = new();
}

/// <summary>
/// L10N 基准 sidecar，全语言共享一个 SignatureId（Language bean 结构签名）。
/// 所有语言共享同一份 id 集合（id×语言矩阵，缺失语言列写空串），
/// 因此 Keys 只记一次，各语言 LangSidecar.Hashes 按下标与 Keys 对齐。
/// v2（spec 2026-08-22）：键为显式 int 语言 id（语言表 id 列），不再有 string key。
/// </summary>
public class L10NSidecar
{
    public string SignatureId { get; set; } = "";

    /// <summary>
    /// id 注册表（活 id ∪ 墓碑）回写区：基准与增量运行末尾都会回写（幂等关键）。
    /// 旧格式 sidecar（string Key/Keys，v1）读入时整体视为空（迁移即全量重基准，无兼容负担）。
    /// </summary>
    public List<KeyEntry> KeyEntries { get; set; } = new();

    /// <summary>
    /// 基准快照：基准时刻活 id 的紧凑视图（升序；墓碑不进快照），增量 diff 的比对基准。
    /// 增量 run 只回写 KeyEntries，不动本快照（累计对基准语义）。
    /// </summary>
    public List<int> Keys { get; set; } = new();

    public Dictionary<string, LangSidecar> Languages { get; set; } = new();

    /// <summary>
    /// L10N 管线里的语言表（LanguageCode/LanguageText 等）的 (ContentHash, Stamp)，供下次基准做表级戳 gating。
    /// 复用 TableSidecarEntry（仅用 ContentHash+Stamp，其余字段空）。
    /// </summary>
    public Dictionary<string, TableSidecarEntry> Tables { get; set; } = new();
}

/// <summary>
/// id 注册表条目：Id = 语言表显式 int id（v2 中 id 即键，条目位置无下标语义）。
/// Deleted=true 为墓碑（id 已从语言表删除，槽位信息保留供增量 diff，id 永不复用）。
/// </summary>
public class KeyEntry
{
    public int Id { get; set; }

    public bool Deleted { get; set; }
}

/// <summary>
/// 单种语言在 L10N sidecar 中的条目：与 L10NSidecar.Keys 下标对齐的 MD5(value) 列表，
/// 外加该语言的整语言内容指纹（gating）与版本戳。
/// </summary>
public class LangSidecar
{
    public List<string> Hashes { get; set; } = new();

    /// <summary>该语言整语言文件的内容指纹（MD5），供下次基准 Stamp gating。</summary>
    public string ContentHash { get; set; } = "";

    /// <summary>该语言内容版本戳（unix 秒），checksumconfig per-language 行下发同值。</summary>
    public long Stamp { get; set; }
}

/// <summary>
/// 增量导出产出的 manifest（_delta.manifest / _l10n.delta.manifest）。
/// 服务器据此 + 客户端上报的 checksum 判定发哪些 patch。
/// </summary>
public class DeltaManifest
{
    public string BaselineSignatureId { get; set; } = "";

    public string SidecarPath { get; set; } = "";

    public List<DeltaManifestEntry> ChangedTables { get; set; } = new();
}

/// <summary>
/// manifest 中单个变化表的条目。
/// </summary>
public class DeltaManifestEntry
{
    public string Table { get; set; } = "";

    public int UpsertCount { get; set; }

    public int DeleteCount { get; set; }

    public string PatchFile { get; set; } = "";

    /// <summary>
    /// 该表/语言应用本 patch 后应更新到的版本戳（unix 秒）。客户端 apply 后据此更新本地戳，下次登录上报新值。
    /// </summary>
    public long Stamp { get; set; }
}
