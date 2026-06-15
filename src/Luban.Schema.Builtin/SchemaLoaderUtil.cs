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

using Luban.Defs;
using Luban.RawDefs;
using Luban.Utils;
using System.IO;
using System.Text.RegularExpressions;

namespace Luban.Schema.Builtin;

public static class SchemaLoaderUtil
{
    public static List<string> CreateGroups(string s)
    {
        return s.Split(',', ';').Select(x => x.Trim()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
    }

    private static readonly Regex s_identifierRegex = new Regex(@"^[A-Za-z_][A-Za-z0-9_]*$");

    // 合法标识符校验：以字母或下划线开头，仅由字母、数字、下划线组成
    public static bool IsValidIdentifier(string name)
    {
        return !string.IsNullOrEmpty(name) && s_identifierRegex.IsMatch(name);
    }

    // 从 input 列（可逗号分隔、含 @sheet 语法）取第一个数据文件的文件名（去路径与扩展名）。
    // 复用 FileUtil.SplitFileAndSheetName，与 DataLoaderManager 解析 input 的方式保持一致。
    public static string GetFirstFileBaseName(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return "";
        }
        string first = input.Split(',', ';')[0].Trim();
        if (string.IsNullOrWhiteSpace(first))
        {
            return "";
        }
        var (file, _) = FileUtil.SplitFileAndSheetName(FileUtil.Standardize(first));
        return Path.GetFileNameWithoutExtension(file);
    }

    public static RawTable CreateTable(string schemaFile, string name, string module, string valueType, string index, string mode, string group,
        string comment, bool readSchemaFromFile, string input, string tags, string outputFileName)
    {
        var p = new RawTable()
        {
            Name = name,
            Namespace = module,
            ValueType = valueType,
            ReadSchemaFromFile = readSchemaFromFile,
            Index = index,
            Groups = CreateGroups(group),
            Comment = comment,
            Mode = ConvertMode(schemaFile, name, mode, index),
            Tags = DefUtil.ParseAttrs(tags),
            OutputFile = outputFileName,
        };
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new Exception($"定义文件:{schemaFile} table:'{p.Name}' name:'{p.Name}' 不能为空");
        }
        if (string.IsNullOrWhiteSpace(valueType))
        {
            throw new Exception($"定义文件:{schemaFile} table:'{p.Name}' value_type:'{valueType}' 不能为空");
        }
        p.InputFiles.AddRange(input.Split(',').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)));

        // if (!string.IsNullOrWhiteSpace(patchInput))
        // {
        //     foreach (var subPatchStr in patchInput.Split('|').Select(s => s.Trim()).Where(s => !string.IsNullOrWhiteSpace(s)))
        //     {
        //         var nameAndDirs = subPatchStr.Split(':');
        //         if (nameAndDirs.Length != 2)
        //         {
        //             throw new Exception($"定义文件:{schemaFile} table:'{p.Name}' patch_input:'{subPatchStr}' 定义不合法");
        //         }
        //         var patchDirs = nameAndDirs[1].Split(',', ';').ToList();
        //         if (!p.PatchInputFiles.TryAdd(nameAndDirs[0], patchDirs))
        //         {
        //             throw new Exception($"定义文件:{schemaFile} table:'{p.Name}' patch_input:'{subPatchStr}' 子patch:'{nameAndDirs[0]}' 重复");
        //         }
        //     }
        // }

        return p;
    }

    public static TableMode ConvertMode(string schemaFile, string tableName, string modeStr, string indexStr)
    {
        TableMode mode;
        string[] indexs = indexStr.Split(',', '+');
        switch (modeStr)
        {
            case "one":
            case "single":
            case "singleton":
            {
                if (!string.IsNullOrWhiteSpace(indexStr))
                {
                    throw new Exception($"定义文件:{schemaFile} table:'{tableName}' mode={modeStr} 是单例表，不支持定义index属性");
                }
                mode = TableMode.ONE;
                break;
            }
            case "map":
            {
                if (!string.IsNullOrWhiteSpace(indexStr) && indexs.Length > 1)
                {
                    throw new Exception($"定义文件:'{schemaFile}' table:'{tableName}' 是单主键表，index:'{indexStr}'不能包含多个key");
                }
                mode = TableMode.MAP;
                break;
            }
            case "list":
            {
                mode = TableMode.LIST;
                break;
            }
            case "":
            {
                if (string.IsNullOrWhiteSpace(indexStr) || indexs.Length == 1)
                {
                    mode = TableMode.MAP;
                }
                else
                {
                    mode = TableMode.LIST;
                }
                break;
            }
            default:
            {
                throw new ArgumentException($"不支持的 mode:{modeStr}");
            }
        }
        return mode;
    }

    public static RawField CreateField(string schemaFile, string name, string alias, string type, string group,
        string comment, string tags, string variants,
        bool ignoreNameValidation)
    {
        var f = new RawField()
        {
            Name = name,
            Alias = alias,
            Groups = CreateGroups(group),
            Comment = comment,
            Tags = DefUtil.ParseAttrs(tags),
            Variants = DefUtil.ParseVariant(variants),
            NotNameValidation = ignoreNameValidation,
        };

        f.Type = type;

        //FillValueValidator(f, refs, "ref");
        //FillValueValidator(f, path, "path"); // (ue4|unity|normal|regex);xxx;xxx
        //FillValueValidator(f, range, "range");

        //FillValidators(defileFile, "key_validator", keyValidator, f.KeyValidators);
        //FillValidators(defileFile, "value_validator", valueValidator, f.ValueValidators);
        //FillValidators(defileFile, "validator", validator, f.Validators);
        return f;
    }
}
