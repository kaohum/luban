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

    public Dictionary<string, string> RowHashes { get; set; } = new();
}

/// <summary>
/// L10N 基准 sidecar，全语言共享一个 SignatureId（Language bean 结构签名）。
/// per-语言记录 key -> MD5(value)。
/// </summary>
public class L10NSidecar
{
    public string SignatureId { get; set; } = "";

    public Dictionary<string, LangSidecar> Languages { get; set; } = new();
}

/// <summary>
/// 单种语言在 L10N sidecar 中的条目。
/// </summary>
public class LangSidecar
{
    public Dictionary<string, string> RowHashes { get; set; } = new();
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
}
