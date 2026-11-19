// plan sidecar 装载：bin-row v3 二阶段代码生成用（导出期 plan → 生成期特化常量）。
// sidecar 内容与数据文件头 plan 段逐字节相同（含 planCount 前缀），哈希用 FNV1a64，与运行时 TableIndex 口径一致。

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Luban.Defs;

namespace Luban.CSharp;

public sealed class BinRowPlanField
{
    public string Name { get; set; }
    public int Kind { get; set; }
    public int Width { get; set; }
    public bool Nullable { get; set; }
    public int Off { get; set; }
    public long Min { get; set; }
    public long[] Palette { get; set; }
}

public sealed class BinRowPlanTable
{
    public ulong Hash { get; set; }
    public int Stride { get; set; }
    public Dictionary<string, BinRowPlanField> FieldsByName { get; set; }
}

internal static class BinRowPlanStore
{
    private static readonly Dictionary<string, BinRowPlanTable> s_cache = new(StringComparer.OrdinalIgnoreCase);
    private static string s_loadedDir;

    public static BinRowPlanTable Get(DefTable table)
    {
        string dir = EnvManager.Current.GetOptionOrDefault("cs-bin", "planDir", true, "");
        if (string.IsNullOrWhiteSpace(dir))
        {
            throw new Exception("[cs-bin] 未配置 -x cs-bin.planDir：bin-row v3 代码生成需要导出期产生的 plan sidecar（先跑 -d bin-row 段）");
        }
        if (s_loadedDir != dir)
        {
            s_cache.Clear();
            s_loadedDir = dir;
        }
        string key = table.OutputDataFile;
        if (s_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }
        string path = Path.Combine(dir, key + ".plan");
        if (!File.Exists(path))
        {
            throw new Exception($"[cs-bin] plan sidecar 缺失: {path}（先跑 -d bin-row 且配置 -x bin-row.planDir=<同一目录>）");
        }
        var plan = Parse(File.ReadAllBytes(path));
        s_cache[key] = plan;
        return plan;
    }

    private static BinRowPlanTable Parse(byte[] b)
    {
        var table = new BinRowPlanTable
        {
            Hash = Fnv1a64(b),
            FieldsByName = new Dictionary<string, BinRowPlanField>(StringComparer.Ordinal),
        };
        int p = 0;
        int planCount = ReadVarint(b, ref p);
        int stride = 0;
        for (int i = 0; i < planCount; i++)
        {
            var f = new BinRowPlanField();
            f.Kind = b[p++];
            int nl = ReadVarint(b, ref p);
            f.Name = nl > 0 ? Encoding.UTF8.GetString(b, p, nl) : string.Empty;
            p += nl;
            f.Width = b[p++];
            f.Nullable = b[p++] != 0;
            if (f.Kind == 1) // Narrow
            {
                f.Min = ReadI64(b, p);
                p += 8;
            }
            else if (f.Kind == 2) // Palette
            {
                int vw = b[p++];
                int cnt = ReadVarint(b, ref p);
                f.Palette = new long[cnt];
                for (int k = 0; k < cnt; k++)
                {
                    f.Palette[k] = ReadSigned(b, p, vw);
                    p += vw;
                }
            }
            f.Off = stride;
            stride += f.Width + (f.Nullable ? 1 : 0);
            table.FieldsByName[f.Name] = f;
        }
        table.Stride = stride;
        return table;
    }

    private static long ReadSigned(byte[] b, int o, int width)
    {
        switch (width)
        {
            case 1: return (sbyte)b[o];
            case 2: return (short)(b[o] | (b[o + 1] << 8));
            case 4: return (int)(uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
            default: return ReadI64(b, o);
        }
    }

    private static long ReadI64(byte[] b, int o)
    {
        return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24))
            | ((long)(uint)(b[o + 4] | (b[o + 5] << 8) | (b[o + 6] << 16) | (b[o + 7] << 24)) << 32);
    }

    private static int ReadVarint(byte[] b, ref int p)
    {
        byte c = b[p];
        if (c < 0x80) { p += 1; return c; }
        if ((c & 0xC0) == 0x80) { int v = ((c & 0x3F) << 8) | b[p + 1]; p += 2; return v; }
        if ((c & 0xE0) == 0xC0) { int v = ((c & 0x1F) << 16) | (b[p + 1] << 8) | b[p + 2]; p += 3; return v; }
        int v4 = (int)(((c & 0x0F) << 24) | (b[p + 1] << 16) | (b[p + 2] << 8) | b[p + 3]);
        p += 4;
        return v4;
    }

    private static ulong Fnv1a64(byte[] data)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte x in data)
        {
            h ^= x;
            h *= 1099511628211UL;
        }
        return h;
    }
}