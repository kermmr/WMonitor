namespace WMonitor.Metrics;

/// <summary>
/// 详情面板「TOP 进程」区块（v0.35）的数据选择：从按应用聚合的快照里挑排序前 N。
/// 纯函数，单测覆盖（PanelTopTests）；绘制与点击在 DetailPanel。
/// </summary>
public static class PanelTopList
{
    /// <summary>排序键下的数值（CPU/GPU 百分比、显存专用字节）。</summary>
    public static double Value(AppMetric a, PanelTopSort sort) => sort switch
    {
        PanelTopSort.Gpu => a.GpuPercent,
        PanelTopSort.Vram => a.GpuDedicatedBytes,
        _ => a.CpuPercent
    };

    /// <summary>榜单行的展示文本：百分比 <c>0.#%</c>；显存 ≥1GB 一位小数、否则整数 MB（与详情区口径一致）。</summary>
    public static string ValueText(AppMetric a, PanelTopSort sort) => sort switch
    {
        PanelTopSort.Gpu => $"{a.GpuPercent:0.#}%",
        PanelTopSort.Vram => a.GpuDedicatedBytes >= 1073741824
            ? $"{a.GpuDedicatedBytes / 1073741824.0:0.0} GB"
            : $"{a.GpuDedicatedBytes / 1048576.0:0} MB",
        _ => $"{a.CpuPercent:0.#}%"
    };

    /// <summary>
    /// 挑排序前 N：系统进程组永不入榜（svchost/系统项常驻占显存，摆出来全是噪音）；
    /// 值为 0 的应用不入榜（CPU 全闲时不摆三行 0% 的空架子，显存榜天然有值）。
    /// 返回降序，最多 n 项。
    /// </summary>
    public static List<AppMetric> Pick(IEnumerable<AppMetric> apps, PanelTopSort sort, int n)
    {
        var list = new List<AppMetric>();
        foreach (var a in apps)
        {
            if (a.IsSystem) continue;
            if (Value(a, sort) <= 0) continue;
            list.Add(a);
        }
        list.Sort((x, y) => Value(y, sort).CompareTo(Value(x, sort)));
        return list.Count > n ? list.GetRange(0, n) : list;
    }
}
