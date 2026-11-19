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

using System.Reflection;

namespace Luban.OutputSaver;

public abstract class OutputSaverBase : IOutputSaver
{
    public virtual string Name => GetType().GetCustomAttribute<OutputSaverAttribute>().Name;

    protected virtual string GetOutputDir(OutputFileManifest manifest)
    {
        string optionName = manifest.OutputType == OutputType.Code
            ? BuiltinOptionNames.OutputCodeDir
            : BuiltinOptionNames.OutputDataDir;
        return EnvManager.Current.GetOption($"{manifest.TargetName}", optionName, true);
    }

    protected virtual void BeforeSave(OutputFileManifest outputFileManifest, string outputDir)
    {

    }

    protected virtual void PostSave(OutputFileManifest outputFileManifest, string outputDir)
    {

    }

    public virtual void Save(OutputFileManifest outputFileManifest)
    {
        string outputDir = GetOutputDir(outputFileManifest);
        BeforeSave(outputFileManifest, outputDir);
        // 顺序落盘：manifest 同名条目语义须为"后者覆盖前者"（组合导出器在 compact 之后追加
        // bin-row 覆盖条目，并行保存会造成同路径竞态、随机胜者）
        foreach (var outputFile in outputFileManifest.DataFiles)
        {
            SaveFile(outputFileManifest, outputDir, outputFile);
        }
        PostSave(outputFileManifest, outputDir);
    }

    public abstract void SaveFile(OutputFileManifest fileManifest, string outputDir, OutputFile outputFile);
}
