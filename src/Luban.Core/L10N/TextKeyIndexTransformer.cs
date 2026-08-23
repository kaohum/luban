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
using System.Globalization;
using Luban.Datas;
using Luban.DataTransformer;
using Luban.DataVisitors;
using Luban.Types;

namespace Luban.L10N
{
    /// <summary>
    /// 把 text 标签的 DString(int 字面量单元格,如 "10034")改写为 DInt(显式语言 id)。
    /// 静态校验三态(spec 2026-08-22 D4):合法 id→DInt(id);parse 失败/id 不在活 id 集→
    /// 哨兵 -1 + [lan-index][missing-id] 告警并收集(R12 语义,不阻塞);空→哨兵 -1。
    /// 不再查 string 注册表——v2 语言 id 在语言表 id 列,由 L10NKeyIndex.LiveIds 提供校验集。
    /// Apply 入口的 (table, field, row) 仅用于错误信息;容器/bean 内嵌 text 字段经由整体重建路径处理。
    /// </summary>
    public class TextKeyIndexTransformer : DataTransfomerBase, IDataFuncVisitor2<DType>
    {
        private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly HashSet<int> _liveIds;
        private readonly string _table;
        private readonly string _field;
        private readonly string _row;
        private readonly SortedSet<string> _missingIds;

        public TextKeyIndexTransformer(HashSet<int> liveIds, string table, string field, string row,
            SortedSet<string> missingIds)
        {
            _liveIds = liveIds;
            _table = table;
            _field = field;
            _row = row;
            _missingIds = missingIds;
        }

        /// <summary>本次转换中缺失/非法的语言 id(单元格原文)集合,调用方可据此汇总告警。</summary>
        public IReadOnlyCollection<string> MissingIds => _missingIds;

        /// <summary>便捷入口,等价于 data.Apply(this, type) / Transform(data, type)。</summary>
        public DType Apply(DType data, TType type)
        {
            return Transform(data, type);
        }

        DType IDataFuncVisitor2<DType>.Accept(DString data, TType type)
        {
            if (!type.HasTag("text"))
            {
                return data;
            }
            var cell = data.Value;
            if (string.IsNullOrEmpty(cell))
            {
                // 空 text 字段哨兵 -1;运行时据此跳过查表
                s_logger.Warn("[lan-index] 表 {Table} 字段 {Field} 行 {Row} 的 text 字段为空,导出为哨兵 -1", _table, _field, _row);
                return DInt.ValueOf(-1);
            }
            if (int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && _liveIds.Contains(id))
            {
                return DInt.ValueOf(id);
            }
            s_logger.Warn("[lan-index][missing-id] 表 {Table} 行 {Row} 的 text 值 '{Cell}' 不是语言表中的合法 id,该格导出为哨兵 -1(运行时空文案);请在语言表补充该 id 或修正引用", _table, _row, cell);
            _missingIds.Add(cell);
            return DInt.ValueOf(-1);
        }
    }
}
