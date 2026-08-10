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
    /// L10N delta patch magic（4 字节 ASCII "LLP1"）。
    /// </summary>
    public const string MagicL10N = "LLP1";

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
