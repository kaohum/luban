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

public static class BuiltinOptionNames
{
    public const string InputDataDir = "inputDataDir";

    public const string OutputCodeDir = "outputCodeDir";

    public const string OutputDataDir = "outputDataDir";

    public const string OutputCodeExtension = "outputCodeExtension";

    public const string OutputDataExtension = "fileExt";

    public const string CodeStyle = "codeStyle";

    public const string DataExporter = "dataExporter";

    public const string CodePostprocess = "codePostprocess";

    public const string DataPostprocess = "dataPostprocess";

    public const string OutputSaver = "outputSaver";

    public const string CleanUpOutputDir = "cleanUpOutputDir";

    public const string SchemaCollectorFamily = "schemaCollector";

    public const string L10NFamily = "l10n";

    public const string L10NProviderName = "provider";

    public const string L10NTextFilePath = "textFile.path";

    public const string L10NTextFileKeyFieldName = "textFile.keyFieldName";
    public const string L10NTextFileKeyFieldDesc = "textFile.keyFieldDesc";

    public const string L10NTextFileLanguageFieldName = "textFile.languageFieldName";

    public const string L10NConvertTextKeyToValue = "convertTextKeyToValue";

    //public const string L10NUnknownTextKeyListOutputFile = "unknownTextKeyListOutputFile";

    public const string L10NTextListFile = "textListFile";

    /// <summary>
    /// 导表期 text 单元格非法语言 id 报告文件（l10n.missingIdsReport，全局选项，非 per-space）。
    /// 每次（基准/增量）导出全量刷新：一行一个非法格子（表,行标识,列,填写值,原因），按（表,行标识,列）
    /// 稳定排序保证幂等；零缺失时写仅含表头的空报告；UTF-8 BOM + CRLF（Excel 友好）。
    /// 空单元格与 ## 注释行不计入。显式置空 = 关闭报告。默认 Output/missing_language_ids.csv，相对 CWD 解析。
    /// </summary>
    public const string L10NMissingIdsReport = "missingIdsReport";

    /// <summary>
    /// text 单元格非法语言 id 告警静默开关（l10n.silentMissingWarn，全局布尔选项，默认 false = 现状）。
    /// true 时仍做静态校验、非法格导出哨兵 -1、逐格收集条目（数据与报告内容零影响），但跳过逐格
    /// [lan-index][missing-id] 与空单元格 WARN、跳过 [lan-index][missing-id-summary] 汇总告警、
    /// 不写 missingIdsReport CSV（报告由客户端 omnibus 调用独占；服务器等只做数据导出的重复调用不重复告警）。
    /// </summary>
    public const string L10NSilentMissingWarn = "silentMissingWarn";

    public const string TypeMapperType = "type";

    public const string TypeMapperConstructor = "constructor";

    public const string PathValidatorFamily = "pathValidator";

    public const string PathValidatorRootDir = "rootDir";

    public const string NamingConvention = "namingConvention";

    public const string LineEnding = "lineEnding";

    public const string FileEncoding = "fileEncoding";

    /// <summary>
    /// 代码目标级表排除名单（{targetName}.excludeTables，逗号分隔表名，OrdinalIgnoreCase 匹配 Name 与 FullName）。
    /// 被排除的表不生成表级代码（表类 / Tables manager 条目 / 表派生枚举项），record bean 类不受影响。
    /// </summary>
    public const string ExcludeTables = "excludeTables";

    public const string CsvSourceOutputDir = "csvSourceOutputDir";

    public const string IncrementalFamily = "incremental";

    public const string IncrementalSidecarPath = "incremental.sidecarPath";

    public const string IncrementalOutputDir = "incremental.outputDir";

    /// <summary>
    /// 本次发布的批次时间戳（unix 秒）。脚本发布开始时算一次，传给 client/server/lang 三次基准调用，
    /// 保证"内容变了"时三方写出同一个新戳。未传时回退到当前 unix 秒（仅单次调用自洽）。
    /// </summary>
    public const string IncrementalExportStamp = "incremental.exportStamp";
}
