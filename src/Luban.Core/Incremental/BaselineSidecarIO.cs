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

using System.IO;
using System.Text.Json;

namespace Luban.Incremental;

/// <summary>
/// 基准 sidecar JSON 读写（System.Text.Json）。
/// 普通表 sidecar 和 L10N sidecar 各用独立文件，格式由各自的 POCO 模型决定。
/// </summary>
public static class BaselineSidecarIO
{
    private static readonly JsonSerializerOptions s_opt = new() { WriteIndented = true };

    /// <summary>
    /// 加载普通表基准 sidecar。
    /// </summary>
    public static BaselineSidecar Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<BaselineSidecar>(json) ?? new BaselineSidecar();
    }

    /// <summary>
    /// 保存普通表基准 sidecar（WriteIndented=true，自动建目录）。
    /// </summary>
    public static void Save(string path, BaselineSidecar sidecar)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(sidecar, s_opt));
    }

    /// <summary>
    /// 保存 L10N 基准 sidecar（WriteIndented=true，自动建目录）。
    /// </summary>
    public static void SaveL10N(string path, L10NSidecar sidecar)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        File.WriteAllText(path, JsonSerializer.Serialize(sidecar, s_opt));
    }

    /// <summary>
    /// 加载 L10N 基准 sidecar。
    /// </summary>
    public static L10NSidecar LoadL10N(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<L10NSidecar>(json) ?? new L10NSidecar();
    }
}
