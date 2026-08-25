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
using Luban.Defs;
using Luban.Types;

namespace Luban.L10N
{
    /// <summary>
    /// 把 text 标签的 DString(int 字面量单元格,如 "10034")改写为 DInt(显式语言 id)。
    /// 静态校验三态(spec 2026-08-22 D4):合法 id->DInt(id);parse 失败/id 不在活 id 集->
    /// 哨兵 -1 + [lan-index][missing-id] 告警并收集(R12 语义,不阻塞);空->哨兵 -1。
    /// 不再查 string 注册表--v2 语言 id 在语言表 id 列,由 L10NKeyIndex.LiveIds 提供校验集。
    /// 非法格子按 (表,行标识,列,填写值,原因) 逐格收集(供 missing_language_ids.csv 报告);
    /// 空 text 单元格按设计合法,不收集。bean 内嵌 text 字段经由 VisitBeanField 钩子携带字段路径。
    /// silent 模式(l10n.silentMissingWarn):校验/导出 -1/收集不变,仅静默逐格 WARN(客户端 omnibus 调用独占告警)。
    /// </summary>
    public class TextKeyIndexTransformer : DataTransfomerBase, IDataFuncVisitor2<DType>
    {
        private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

        private readonly HashSet<int> _liveIds;
        private readonly string _table;
        private readonly string _field;
        private readonly string _row;
        private readonly List<MissingTextIdEntry> _missingEntries;
        private readonly bool _silent;

        /// <summary>bean 递归中的字段路径栈（顶层字段为栈底，'.' 连接成列名）。</summary>
        private readonly List<string> _fieldPath = new();

        public TextKeyIndexTransformer(HashSet<int> liveIds, string table, string field, string row,
            List<MissingTextIdEntry> missingEntries, bool silent = false)
        {
            _liveIds = liveIds;
            _table = table;
            _field = field;
            _row = row;
            _missingEntries = missingEntries;
            _silent = silent;
        }

        /// <summary>本次转换中逐格收集的非法语言 id 明细（一个非法格子一条），供缺失报告与汇总告警。</summary>
        public IReadOnlyList<MissingTextIdEntry> MissingEntries => _missingEntries;

        /// <summary>便捷入口,等价于 data.Apply(this, type) / Transform(data, type)。</summary>
        public DType Apply(DType data, TType type)
        {
            return Transform(data, type);
        }

        /// <summary>当前列名：bean 递归中为字段路径（如 reward.nameId），顶层直用时为构造传入的字段名。</summary>
        private string CurrentField()
        {
            return _fieldPath.Count > 0 ? string.Join(".", _fieldPath) : _field;
        }

        protected override DType VisitBeanField(DBean bean, DefField field, DType value)
        {
            _fieldPath.Add(field.Name);
            try
            {
                return base.VisitBeanField(bean, field, value);
            }
            finally
            {
                _fieldPath.RemoveAt(_fieldPath.Count - 1);
            }
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
                // 空 text 字段哨兵 -1;运行时据此跳过查表(按设计合法,不入缺失报告)
                if (!_silent)
                {
                    s_logger.Warn("[lan-index] 表 {Table} 字段 {Field} 行 {Row} 的 text 字段为空,导出为哨兵 -1", _table, CurrentField(), _row);
                }
                return DInt.ValueOf(-1);
            }
            if (int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) && _liveIds.Contains(id))
            {
                return DInt.ValueOf(id);
            }
            // parse 失败=非数字(旧 string key/自由文本);parse 成功但不在活 id 集=id不存在(含 -1 字面量与陈旧 id)
            bool parsed = int.TryParse(cell, NumberStyles.Integer, CultureInfo.InvariantCulture, out _);
            var reason = parsed ? MissingTextIdReason.IdNotExists : MissingTextIdReason.NotANumber;
            if (!_silent)
            {
                s_logger.Warn("[lan-index][missing-id] 表 {Table} 行 {Row} 列 {Field} 的 text 值 '{Cell}' 不是语言表中的合法 id,该格导出为哨兵 -1(运行时空文案);请在语言表补充该 id 或修正引用", _table, _row, CurrentField(), cell);
            }
            _missingEntries.Add(new MissingTextIdEntry(_table, _row, CurrentField(), cell, reason));
            return DInt.ValueOf(-1);
        }
    }
}
