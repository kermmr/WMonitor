using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;

namespace WMonitor.Metrics;

/// <summary>历史曲线可选指标（顺序固定，不要插在中间）。</summary>
public enum HistoryMetric { Cpu = 0, Ram = 1, Gpu = 2, Vram = 3, Net = 4, Temp = 5, Disk = 6 }

/// <summary>一条采样记录，40 字节定长。定长才能用 <see cref="MemoryMarshal"/> 一次读写，
/// 也才能按固定偏移切分文件（不必解析文本）。</summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct HistoryRecord
{
    public uint Ts;         // Unix 秒
    public float Cpu;       // 0–100
    public float Ram;
    public float Gpu;
    public float Vram;
    public float DiskR;     // KB/s
    public float DiskW;     // KB/s
    public float NetDown;   // KB/s
    public float NetUp;     // KB/s
    public float Temp;      // °C；NaN = 无 NVML

    public const int Size = 40;
}

/// <summary>查询结果。<see cref="Values"/> 长度固定，直接喂给 <c>DetailPanel.DrawSpark</c>。</summary>
public readonly record struct HistorySeries(
    float[] Values, long FromTs, long ToTs, bool FromDisk, int BucketSeconds);

/// <summary>
/// 两级历史：内存环 1 小时（144 KB，供 1m/5m/1h 曲线）+ 磁盘按天文件（3.4 MB/天，供 6h/24h/72h）。
/// 内存只留 1 小时是刻意的：24 小时全放内存要 3.4 MB，会毁掉「41 MB」这个卖点。
/// 全在 UI 线程调用，不加锁。
/// </summary>
public sealed class HistoryStore : IDisposable
{
    /// <summary>内存环容量（1 秒 1 条 → 1 小时）。</summary>
    public const int RingCapacity = 3600;

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("WMHIST01");

    private readonly HistoryRecord[] _ring = new HistoryRecord[RingCapacity];
    private int _head;      // 下一条写入位置
    private int _count;

    private readonly bool _persist;
    private readonly int _retentionHours;
    private readonly string _dir;

    private FileStream? _file;
    private DateTime _fileDate;
    private long _lastFlush;
    private uint _lastTs;
    private bool _disposed;

    public bool Enabled { get; }

    /// <summary>是否落盘（决定面板能不能给 24h 窗口）。</summary>
    public bool CanPersist => _persist;

    /// <summary>目录：%APPDATA%\WMonitor\history（关掉持久化时不会创建）。</summary>
    public static string Dir => Path.Combine(AppConfig.Dir, "history");

    public HistoryStore(bool enabled, bool persist, int retentionHours)
    {
        Enabled = enabled;
        _persist = enabled && persist;
        _retentionHours = Math.Clamp(retentionHours, 1, 24 * 30);
        _dir = Dir;
        if (!Enabled) return;

        if (_persist)
        {
            Prune();
            LoadRecent();
        }
    }

    /// <summary>最早可用时间戳（Unix 秒）；无数据返回 0。</summary>
    public long OldestTs
    {
        get
        {
            if (_count == 0) return 0;
            int idx = (_head - _count + RingCapacity * 2) % RingCapacity;
            return _ring[idx].Ts;
        }
    }

    /// <summary>内存环里的记录数（诊断用）。</summary>
    public int Count => _count;

    /// <summary>磁盘上某天的文件大小（诊断用）。</summary>
    public long FileBytes()
    {
        try
        {
            string p = Path.Combine(_dir, DateTime.Today.ToString("yyyyMMdd") + ".hist");
            return File.Exists(p) ? new FileInfo(p).Length : 0;
        }
        catch { return 0; }
    }

    // ---------------- 写入 ----------------

    public void Push(MetricsSnapshot s, long nowTicks)
    {
        if (!Enabled || _disposed) return;

        uint ts = (uint)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // 节流到 1 秒一条：RefreshMs 配成 500 时也只落一半，保证文件大小可预测（3.4 MB/天）
        if (ts == _lastTs) return;
        _lastTs = ts;

        var r = new HistoryRecord
        {
            Ts = ts,
            Cpu = (float)s.CpuPercent,
            Ram = (float)s.RamPercent,
            Gpu = (float)s.GpuPercent,
            Vram = (float)s.VramPercent,
            DiskR = (float)(s.DiskReadBps / 1024.0),
            DiskW = (float)(s.DiskWriteBps / 1024.0),
            NetDown = (float)(s.NetDownBps / 1024.0),
            NetUp = (float)(s.NetUpBps / 1024.0),
            Temp = double.IsNaN(s.GpuTempC) ? float.NaN : (float)s.GpuTempC,
        };

        _ring[_head] = r;
        _head = (_head + 1) % RingCapacity;
        if (_count < RingCapacity) _count++;

        if (_persist) Append(r, nowTicks);
    }

    private void Append(HistoryRecord r, long nowTicks)
    {
        try
        {
            var today = DateTime.Today;
            if (_file == null || _fileDate != today)
            {
                _file?.Dispose();
                _fileDate = today;
                Directory.CreateDirectory(_dir);
                string path = Path.Combine(_dir, today.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".hist");
                bool fresh = !File.Exists(path) || new FileInfo(path).Length == 0;
                _file = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read, 4096);
                if (fresh) _file.Write(Magic);
            }

            Span<byte> buf = stackalloc byte[HistoryRecord.Size];
            MemoryMarshal.Write(buf, in r);
            _file.Write(buf);

            // 每 60 秒 flush 一次；不做 Flush(true)，避免每秒 fsync 打盘
            if (nowTicks - _lastFlush > Stopwatch.Frequency * 60)
            {
                _lastFlush = nowTicks;
                _file.Flush();
            }
        }
        catch
        {
            // 磁盘满 / 目录被删 / 文件被占用：关掉句柄，下一 tick 重开，不影响贴片
            try { _file?.Dispose(); } catch { }
            _file = null;
        }
    }

    // ---------------- 查询 ----------------

    /// <summary>
    /// 取某指标在 <paramref name="window"/> 内的曲线，输出固定 <paramref name="points"/> 个值。
    /// 每桶取最大值 —— 利用率的价值在尖峰，取平均会把 3 秒满载摊平到看不出来。
    /// </summary>
    public HistorySeries Query(HistoryMetric m, TimeSpan window, int points = 60)
    {
        points = Math.Clamp(points, 3, 240);
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        long from = now - (long)window.TotalSeconds;
        int bucket = Math.Max(1, (int)(window.TotalSeconds / points));

        var acc = new float[points];
        var hit = new bool[points];

        if (!Enabled)
            return new HistorySeries(acc, from, now, false, bucket);

        // 1 小时以内走内存环（快、无 IO）；更长的窗口只能读磁盘
        if (window.TotalSeconds <= RingCapacity)
            AccumulateRing(m, from, acc, hit, points, bucket);
        else
            AccumulateDisk(m, from, now, acc, hit, points, bucket);

        FillGaps(acc, hit);
        return new HistorySeries(acc, from, now, window.TotalSeconds > RingCapacity, bucket);
    }

    private void AccumulateRing(HistoryMetric m, long from, float[] acc, bool[] hit, int points, int bucket)
    {
        for (int i = 0; i < _count; i++)
        {
            int idx = (_head - _count + i + RingCapacity * 2) % RingCapacity;
            var r = _ring[idx];
            if (r.Ts < from) continue;
            Accumulate(m, r, from, acc, hit, points, bucket);
        }
    }

    private void AccumulateDisk(HistoryMetric m, long from, long now, float[] acc, bool[] hit, int points, int bucket)
    {
        if (!_persist) return;   // 没开持久化：长窗口无数据源，返回空曲线

        var day = DateTimeOffset.FromUnixTimeSeconds(from).LocalDateTime.Date;
        var today = DateTime.Today;
        for (; day <= today; day = day.AddDays(1))
        {
            string path = Path.Combine(_dir, day.ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".hist");
            if (!File.Exists(path)) continue;

            byte[] bytes;
            var read = ReadShared(path);
            if (read == null) continue;
            bytes = read;

            if (bytes.Length < Magic.Length + HistoryRecord.Size) continue;
            if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) continue;   // 损坏文件直接跳过

            int n = (bytes.Length - Magic.Length) / HistoryRecord.Size;          // 尾部半条自然被丢弃
            for (int i = 0; i < n; i++)
            {
                var r = MemoryMarshal.Read<HistoryRecord>(bytes.AsSpan(Magic.Length + i * HistoryRecord.Size));
                if (r.Ts < from || r.Ts > now) continue;
                Accumulate(m, r, from, acc, hit, points, bucket);
            }
        }
    }

    private static void Accumulate(HistoryMetric m, in HistoryRecord r, long from,
        float[] acc, bool[] hit, int points, int bucket)
    {
        int b = (int)((r.Ts - from) / bucket);
        if (b < 0) b = 0;
        if (b >= points) b = points - 1;

        float v = Value(m, r);
        if (float.IsNaN(v)) return;
        if (!hit[b] || v > acc[b]) { acc[b] = v; hit[b] = true; }
    }

    private static float Value(HistoryMetric m, in HistoryRecord r) => m switch
    {
        HistoryMetric.Ram => r.Ram,
        HistoryMetric.Gpu => r.Gpu,
        HistoryMetric.Vram => r.Vram,
        HistoryMetric.Net => r.NetDown,
        HistoryMetric.Temp => r.Temp,
        HistoryMetric.Disk => r.DiskR + r.DiskW,
        _ => r.Cpu,
    };

    /// <summary>
    /// 只补「内部空洞」（两端都有数据、中间漏采），首尾空缺保留 0。
    /// 首部不能向前填充：程序刚启动时会把整个 24h 窗口画成一条平线，看着像"一直这么忙"，
    /// 而实际是"那段时间没采"——0 才是诚实的。
    /// </summary>
    private static void FillGaps(float[] acc, bool[] hit)
    {
        int first = Array.IndexOf(hit, true);
        if (first < 0) return;
        int last = Array.LastIndexOf(hit, true);

        float carry = acc[first];
        for (int i = first; i <= last; i++)
        {
            if (hit[i]) carry = acc[i];
            else acc[i] = carry;
        }
    }

    // ---------------- 磁盘维护 ----------------

    /// <summary>
    /// 以共享方式读文件。必须允许 ReadWrite —— 应用自己正握着今天文件的写入句柄，
    /// 用 <c>File.ReadAllBytes</c>（FileShare.Read）会因共享冲突失败，表现为「历史一直是空的」。
    /// </summary>
    private static byte[]? ReadShared(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            using var ms = new MemoryStream((int)Math.Min(fs.Length, 8 << 20));
            fs.CopyTo(ms);
            return ms.ToArray();
        }
        catch { return null; }
    }

    /// <summary>删除超过保留期的日文件。</summary>
    private void Prune()
    {
        try
        {
            if (!Directory.Exists(_dir)) return;
            var cutoff = DateTime.Today.AddDays(-_retentionHours / 24).Date;
            foreach (string f in Directory.GetFiles(_dir, "*.hist"))
            {
                string name = Path.GetFileNameWithoutExtension(f);
                if (!DateTime.TryParseExact(name, "yyyyMMdd", CultureInfo.InvariantCulture,
                        DateTimeStyles.None, out var d)) continue;
                if (d.Date < cutoff) File.Delete(f);
            }
        }
        catch { }
    }

    /// <summary>启动时把最近的文件尾部填进内存环，重启后面板仍能看最近 1 小时。</summary>
    private void LoadRecent()
    {
        try
        {
            var files = new List<string>();
            for (int i = 0; i <= Math.Max(1, _retentionHours / 24); i++)
            {
                string p = Path.Combine(_dir, DateTime.Today.AddDays(-i).ToString("yyyyMMdd", CultureInfo.InvariantCulture) + ".hist");
                if (File.Exists(p)) files.Add(p);
            }
            files.Reverse();   // 旧 → 新

            foreach (string path in files)
            {
                byte[] bytes;
                var read = ReadShared(path);
                if (read == null) continue;
                bytes = read;
                if (bytes.Length < Magic.Length + HistoryRecord.Size) continue;
                if (!bytes.AsSpan(0, Magic.Length).SequenceEqual(Magic)) continue;

                int n = (bytes.Length - Magic.Length) / HistoryRecord.Size;
                // 只取最后 RingCapacity 条，避免大文件全量入环
                int start = Math.Max(0, n - RingCapacity);
                for (int i = start; i < n; i++)
                {
                    _ring[_head] = MemoryMarshal.Read<HistoryRecord>(
                        bytes.AsSpan(Magic.Length + i * HistoryRecord.Size));
                    _head = (_head + 1) % RingCapacity;
                    if (_count < RingCapacity) _count++;
                }
            }
        }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _file?.Flush(); _file?.Dispose(); } catch { }
        _file = null;
    }
}
