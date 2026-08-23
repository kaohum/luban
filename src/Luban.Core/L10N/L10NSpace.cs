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

namespace Luban.L10N;

/// <summary>
/// 一个语言 key space:一组语言表 + 独立的下标注册表(sidecar)+ 输出目录。
/// </summary>
public class L10NSpace
{
    public string Name { get; set; } = "";

    /// <summary>语言表名单(表全名或短名,逗号分隔配置)。</summary>
    public List<string> Tables { get; set; } = new();

    /// <summary>true=数组格式 bin + text 字段转 int;false=保持现有 string 字典格式。</summary>
    public bool IndexMode { get; set; }

    public List<string> Languages { get; set; } = new();

    public string OutputFile { get; set; } = "languageconfig";

    /// <summary>输出子目录(相对 dataTarget 输出根目录)。</summary>
    public string OutputDir { get; set; } = "";

    public string KeyFieldName { get; set; } = "key";

    public string KeyFieldDesc { get; set; } = "zh_CN";

    /// <summary>v2(D7):name 列(访问器命名/人读);语言表无该列时访问器名退化为 id/key 派生(如 server space)。</summary>
    public string NameFieldName { get; set; } = "name";

    /// <summary>
    /// AF-3/AF-16:是否为该 space 生成 cs-l10n-language 静态访问器类(默认 true,向后兼容)。
    /// false 时 cs-l10n-language 对该 space 不产出任何文件;数据导出/sidecar/id 注册表不受影响。
    /// </summary>
    public bool GenAccessors { get; set; } = true;

    /// <summary>本 space 的 sidecar 路径(注册表 + 基准快照)。</summary>
    public string SidecarPath { get; set; } = "";

    /// <summary>LoadDatas 末尾构建的下标注册表(分配后)。</summary>
    public L10NKeyIndex KeyIndex { get; set; }
}
