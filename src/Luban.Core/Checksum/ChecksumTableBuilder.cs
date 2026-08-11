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

using Luban.Datas;
using Luban.Defs;
using Luban.RawDefs;
using Luban.Types;

namespace Luban.Checksum;

/// <summary>
/// 配置表校验和数据构建器
/// 创建虚拟的 Checksum 表，填充所有表的 MD5 数据
/// </summary>
public static class ChecksumTableBuilder
{
    public const string ChecksumTableName = "ChecksumConfig";
    private const string ChecksumBeanName = "ChecksumInfo";

    /// <summary>
    /// 创建虚拟的 Checksum 表定义
    /// </summary>
    /// <param name="assembly">DefAssembly 实例</param>
    /// <param name="outputFile">输出文件名（不含扩展名），为 null 或空时使用默认命名规则</param>
    public static DefTable CreateChecksumTableDef(DefAssembly assembly, string outputFile = null)
    {
        // 检查是否已经创建过
        if (assembly.TablesByFullName.TryGetValue(ChecksumTableName, out var existingTable))
        {
            return existingTable;
        }

        // 1. 创建 Checksum Bean 定义
        var rawBean = new RawBean
        {
            Name = ChecksumBeanName,
            Namespace = "",
            Parent = "",
            Comment = "配置表校验和信息",
            Groups = new List<string>(),
            Fields = new List<RawField>
            {
                new RawField
                {
                    Name = "TableName",
                    Type = "string",
                    Comment = "表名",
                    Groups = new List<string>()
                },
                new RawField
                {
                    Name = "Stamp",
                    Type = "long",
                    Comment = "内容版本戳（unix 秒，内容相关：内容没变则沿用）",
                    Groups = new List<string>()
                },
                new RawField
                {
                    Name = "SignatureId",
                    Type = "string",
                    Comment = "结构签名（MD5，全字段，前后端一致）",
                    Groups = new List<string>()
                }
            }
        };

        var defBean = new DefBean(rawBean);
        defBean.Assembly = assembly;

        // 先添加 Bean 到 Assembly，让它可以被其他类型引用
        assembly.AddType(defBean);

        // 编译 Bean（必须按顺序调用 PreCompile、Compile、PostCompile）
        defBean.PreCompile();
        defBean.Compile();
        defBean.PostCompile();

        // 2. 创建 Checksum 表定义
        var rawTable = new RawTable
        {
            Name = ChecksumTableName,
            Namespace = "",
            Index = "TableName",
            ValueType = defBean.FullName,
            Mode = TableMode.LIST,
            Comment = "配置表校验和汇总表",
            InputFiles = new List<string> { "__checksum__" },
            Groups = new List<string>(),
            OutputFile = !string.IsNullOrWhiteSpace(outputFile) ? outputFile : null
            // null 时使用默认命名规则：ChecksumConfig -> checksumconfig.bytes
        };

        var defTable = new DefTable(rawTable);
        defTable.Assembly = assembly;
        defTable.PreCompile();
        defTable.Compile();
        defTable.PostCompile();

        // 将表添加到 Assembly
        assembly.AddCfgTable(defTable);

        return defTable;
    }

    /// <summary>
    /// 创建 Checksum 表的数据记录
    /// </summary>
    public static List<Record> CreateChecksumRecords(DefTable checksumTable, IEnumerable<DefTable> tables)
    {
        var records = new List<Record>();

        foreach (var table in tables)
        {
            if (string.IsNullOrEmpty(table.ContentHash))
            {
                continue;  // 跳过没有数据的表
            }

            records.Add(CreateChecksumRecord(checksumTable, table.Name, table.Stamp, table.SignatureId ?? ""));
        }

        return records;
    }

    /// <summary>
    /// 创建单条 Checksum 记录（TableName / Stamp / SignatureId）。
    /// 供普通表逐个创建，也供 L10N 管线按语言注入 per-language 行（TableName=语言名）。
    /// </summary>
    public static Record CreateChecksumRecord(DefTable checksumTable, string tableName, long stamp, string signatureId)
    {
        var tbean = checksumTable.ValueTType;
        var defBean = tbean.DefBean;

        // 获取字段类型
        var tableNameField = defBean.HierarchyFields[0];    // TableName 字段
        var signatureIdField = defBean.HierarchyFields[2];  // SignatureId 字段（[1] 是 Stamp，long 无需 type）

        // 创建 DBean 数据
        var fields = new List<DType>
        {
            DString.ValueOf(tableNameField.CType, tableName),           // TableName
            DLong.ValueOf(stamp),                                       // Stamp
            DString.ValueOf(signatureIdField.CType, signatureId ?? "")  // SignatureId
        };

        var dbean = new DBean(tbean, defBean, fields);

        // 创建 Record
        return new Record(dbean, "__checksum__", new List<string> { Record.DefaultTag });
    }
}
