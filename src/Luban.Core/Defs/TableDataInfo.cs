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

﻿using System;
﻿using Luban.Datas;
using Luban.Utils;

namespace Luban.Defs;

// 索引键包装类，支持单个字段或组合字段
public class IndexKey : IEquatable<IndexKey>
{
    public List<DType> Keys { get; }

    public IndexKey(List<DType> keys)
    {
        Keys = keys;
    }

    public IndexKey(DType singleKey)
    {
        Keys = new List<DType> { singleKey };
    }

    public override bool Equals(object obj)
    {
        return obj is IndexKey other && Equals(other);
    }

    public bool Equals(IndexKey other)
    {
        if (other == null || Keys.Count != other.Keys.Count)
        {
            return false;
        }

        for (int i = 0; i < Keys.Count; i++)
        {
            if (!Keys[i].Equals(other.Keys[i]))
            {
                return false;
            }
        }
        return true;
    }

    public override int GetHashCode()
    {
        int hash = 17;
        foreach (var key in Keys)
        {
            hash = hash * 31 + (key?.GetHashCode() ?? 0);
        }
        return hash;
    }

    public override string ToString()
    {
        return Keys.Count == 1 ? Keys[0].ToString() : $"({string.Join(", ", Keys)})";
    }
}

public class TableDataInfo
{
    private static readonly NLog.Logger s_logger = NLog.LogManager.GetCurrentClassLogger();

    public DefTable Table { get; }

    public List<Record> MainRecords { get; }

    public List<Record> PatchRecords { get; }

    public List<Record> FinalRecords { get; private set; }

    public Dictionary<DType, Record> FinalRecordMap { get; private set; }

    public Dictionary<string, Dictionary<IndexKey, Record>> FinalRecordMapByIndexs { get; private set; }

    private static bool HasSameTag(Record a, Record b)
    {
        if (a.Tags == null || a.Tags.Count == 0 || b.Tags == null || b.Tags.Count == 0)
        {
            return false;
        }

        foreach (var ta in a.Tags)
        {
            foreach (var tb in b.Tags)
            {
                if (string.Equals(ta, tb, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        return false;
    }

    public TableDataInfo(DefTable table, List<Record> mainRecords, List<Record> patchRecords)
    {
        Table = table;
        MainRecords = mainRecords;
        PatchRecords = patchRecords;

        BuildIndexs();

        int index = 0;
        foreach (var record in FinalRecords)
        {
            record.AutoIndex = index++;
        }

        if (table.IsSingletonTable && FinalRecords.Count != 1)
        {
            //throw new Exception($"配置表 {table.FullName} 是单值表 mode=one,但数据个数:{FinalRecords.Count} != 1");
        }
    }

    private void BuildIndexs()
    {
        List<Record> mainRecords = MainRecords;
        List<Record> patchRecords = PatchRecords;

        // 这么大费周张是为了保证被覆盖的id仍然保持原来的顺序，而不是出现在最后
        int index = 0;
        var recordIndex = new Dictionary<Record, int>();
        var overrideRecords = new HashSet<Record>();
        foreach (var r in mainRecords)
        {
            if (recordIndex.TryAdd(r, index))
            {
                index++;
            }
        }
        if (patchRecords != null)
        {
            foreach (var r in patchRecords)
            {
                if (recordIndex.TryAdd(r, index))
                {
                    index++;
                }
            }
        }

        var table = Table;
        // TODO 有一个微妙的问题，ref检查虽然通过，但ref的记录有可能未导出
        switch (Table.Mode)
        {
            case TableMode.ONE:
            {
                // TODO 如果此单例表使用tag,有多个记录，则patchRecords会覆盖全部。
                // 好像也挺有道理的，毕竟没有key，无法区分覆盖哪个
                if (patchRecords != null && patchRecords.Count > 0)
                {
                    mainRecords = patchRecords;
                }
                FinalRecords = mainRecords;
                break;
            }
            case TableMode.MAP:
            {
                var recordMap = new Dictionary<DType, Record>();
                var recordsByKey = new Dictionary<DType, List<Record>>();
                foreach (Record r in mainRecords)
                {
                    DType key = r.Data.Fields[table.IndexFieldIdIndex];
                    if (!recordsByKey.TryGetValue(key, out var list))
                    {
                        list = new List<Record>();
                        recordsByKey.Add(key, list);
                    }
                    else
                    {
                        foreach (var existed in list)
                        {
                            if (HasSameTag(existed, r))
                            {
                                throw new Exception($@"配置表 '{table.FullName}' 主文件 主键字段:'{table.Index}' 主键值:'{key}' 重复.
        记录1 来自文件:{existed.Source}
        记录2 来自文件:{r.Source}
");
                            }
                        }
                    }
                    list.Add(r);

                    if (!recordMap.ContainsKey(key))
                    {
                        recordMap.Add(key, r);
                    }
                }
                if (patchRecords != null && patchRecords.Count > 0)
                {
                    foreach (Record r in patchRecords)
                    {
                        DType key = r.Data.Fields[table.IndexFieldIdIndex];
                        if (recordMap.TryGetValue(key, out var old))
                        {
                            if (overrideRecords.Contains(old))
                            {
                                throw new Exception($"配置表 '{table.FullName}' 主文件 主键字段:'{table.Index}' 主键值:'{key}' 被patch多次覆盖，请检查patch是否有重复记录");
                            }
                            s_logger.Debug("配置表 {} 分支文件 主键:{} 覆盖 主文件记录", table.FullName, key);
                            mainRecords[recordIndex[old]] = r;
                        }
                        else
                        {
                            mainRecords.Add(r);
                        }
                        overrideRecords.Add(r);
                        recordMap[key] = r;
                    }
                }
                FinalRecords = mainRecords;
                FinalRecordMap = recordMap;
                break;
            }
            case TableMode.LIST:
            {
                if (patchRecords != null && patchRecords.Count > 0)
                {
                    throw new Exception($"配置表 '{table.FullName}' 是list表.不支持patch");
                }
                var recordMapByIndexs = new Dictionary<string, Dictionary<IndexKey, Record>>();
                
                // 遍历每个索引项（可能是单个字段索引或组合字段索引）
                foreach (var indexInfo in table.IndexList)
                {
                    var recordMap = new Dictionary<IndexKey, Record>();
                    var recordsByKey = new Dictionary<IndexKey, List<Record>>();
                    
                    foreach (Record r in mainRecords)
                    {
                        IndexKey key;
                        
                        // 如果是组合索引，需要组合多个字段的值
                        if (indexInfo.IsUnionIndex)
                        {
                            var unionKeys = indexInfo.IndexFieldIdIndexes.Select(idx => r.Data.Fields[idx]).ToList();
                            key = new IndexKey(unionKeys);
                        }
                        else
                        {
                            // 单个字段索引
                            key = new IndexKey(r.Data.Fields[indexInfo.IndexFieldIdIndex]);
                        }
                        
                        // 检查键是否重复
                        if (!recordsByKey.TryGetValue(key, out var list))
                        {
                            list = new List<Record>();
                            recordsByKey.Add(key, list);
                        }
                        else
                        {
//                             foreach (var existed in list)
//                             {
//                                 if (HasSameTag(existed, r))
//                                 {
//                                     throw new Exception($@"配置表 '{table.FullName}' 主文件 主键字段:'{indexInfo.IndexName}' 主键值:'{key}' 重复.
//         记录1 来自文件:{existed.Source}
//         记录2 来自文件:{r.Source}
// ");
//                                 }
//                             }
                        }
                        list.Add(r);
                        recordMap[key] = r;
                    }
                    
                    // 检测是否为多值索引（是否有重复的key）
                    foreach (var kvp in recordsByKey)
                    {
                        if (kvp.Value.Count > 1)
                        {
                            indexInfo.IsMultiValue = true;
                            break;
                        }
                    }
                    
                    recordMapByIndexs.Add(indexInfo.IndexName, recordMap);
                }
                
                this.FinalRecordMapByIndexs = recordMapByIndexs;
                FinalRecords = mainRecords;
                break;
            }
            default:
                throw new Exception($"unknown mode:{Table.Mode}");
        }
    }
}
