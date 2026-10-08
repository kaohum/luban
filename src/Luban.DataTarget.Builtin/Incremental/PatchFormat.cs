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

using Luban.Serialization;

namespace Luban.DataExporter.Builtin.Incremental;

/// <summary>
/// 增量 patch 文件格式常量 + 写出辅助。
/// DLP1 = Delta Patch v1（普通表）；LLP1 = L10N Language Patch v1。
/// </summary>
public static class PatchFormat
{
    /// <summary>
    /// 普通表 delta patch magic（4 字节 ASCII "DLP1"）。
    /// </summary>
    public const string MagicTable = "DLP1";

    /// <summary>
    /// 无稳定行键表的整表替换 patch magic（4 字节 ASCII "DLF1"）。
    /// magic 后的 body 与全表 .bytes 完全一致（[sig][字符串表][count][rows]），
    /// 客户端校验 magic 后复用 Create 全量重建（先释放旧池化容器）。
    /// </summary>
    public const string MagicTableFull = "DLF1";

    /// <summary>
    /// 复合身份索引行键的字段分隔符（如 "techTypeId+level" -> "101+3"），与索引声明语法一致。
    /// string 键字段值含此分隔符会被导出拒绝；float/double 禁止作为身份索引字段（ToString 科学计数法可能含分隔符）。
    /// </summary>
    public const string KeySeparator = "+";

    /// <summary>
    /// L10N delta patch magic（4 字节 ASCII "LLP1"，string key 格式）。
    /// v2（spec 2026-08-22）已退役：语言键改为显式 int id 后无 string-key 增量路径，
    /// 保留常量仅作线上存量 patch 的格式鉴别（旧客户端按 magic 拒绝 LLP2，反之亦然）。
    /// </summary>
    public const string MagicL10N = "LLP1";

    /// <summary>
    /// L10N index 模式 delta patch magic（4 字节 ASCII "LLP2"）。全 id（v2：字段=显式 int 语言 id），无字符串 key。
    /// </summary>
    public const string MagicL10N2 = "LLP2";

    /// <summary>
    /// 把 4 字节 magic 写入 ByteBuf（逐字节 WriteByte，保证字节序与 ASCII 一致）。
    /// </summary>
    public static void WriteMagic(ByteBuf buf, string magic)
    {
        foreach (var c in magic)
        {
            buf.WriteByte((byte)c);
        }
    }
}
