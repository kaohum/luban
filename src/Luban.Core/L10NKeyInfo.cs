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

namespace Luban;

/// <summary>
/// 一条本地化 key 的枚举结果（v2 显式 int id 形态，spec 2026-08-22 D7）。
/// <see cref="Id"/> 烘进访问器 Get(id)；<see cref="FieldName"/> 取自语言表 name 列
/// （name 为空（含表无 name 列的空间，如 server space）时退化为 L_{id} 派生）；<see cref="Desc"/> 供 XML 注释。
/// </summary>
public class L10NKeyInfo
{
    /// <summary>显式 int 语言 id（表内 id 列）。-1 = 无 int id 的过渡形态（旧 string key 单值路径）。</summary>
    public int Id { get; }

    /// <summary>name 列原始值（人读标识）；语言表无 name 列时为 null。</summary>
    public string Name { get; }

    /// <summary>由 name 列（或退化为 id/key）清洗出的合法 C# 标识符（清洗后冲突时带 _2 等后缀）。</summary>
    public string FieldName { get; }

    /// <summary>描述（desc 字段）内容，用于生成 XML 注释。</summary>
    public string Desc { get; }

    public L10NKeyInfo(int id, string name, string fieldName, string desc)
    {
        Id = id;
        Name = name;
        FieldName = fieldName;
        Desc = desc;
    }
}
