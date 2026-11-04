using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using WMonitor.Interop;

namespace WMonitor.Metrics;

/// <summary>
/// 应用图标缓存（plan §3.4，阶段 3）：SHDefExtractIcon(48px) → 磁盘 PNG → 内存 LRU。
///
/// 三层：
/// 1. 内存 LRU（约 220 项，含负缓存项「抽不到」），命中零开销；
/// 2. 磁盘 <c>%APPDATA%\WMonitor\icon-cache\</c>：文件名 = sha1(小写路径)-mtimeTicks.png，
///    exe 更新后 mtime 变化自动失效；同名的 .miss 0 字节文件记录「抽不到」，避免反复重试；
/// 3. 现抽：SHDefExtractIcon（shell32 与资源管理器同源，exe / dll / ico 都支持）。
///
/// 抽取只在 UI 线程 paint 中按需发生（可见行才查表），每 50ms 窗口最多现抽 2 个，
/// 超出的行先回退首字母圆点、下一帧补上 —— 摊薄后 &lt; 0.5 ms/tick（plan §5 图标预算）。
/// 目录文件数超上限时删最旧。仅 UI 线程使用，无锁。Image 归本类所有（淘汰时 Dispose），调用方只读。
/// </summary>
public sealed class IconCache : IDisposable
{
    private const int IconPx = 48;             // 表格 28px @150% DPI = 42px，抽 48 一步到位
    private const int LruCapacity = 220;
    private const int MaxFiles = 512;
    private const int MaxExtractsPerWindow = 2;
    private const long ExtractWindowTicks = 50;   // ms；同窗口共享抽取预算，hover 重绘也不放大

    private readonly string _dir;
    private readonly Dictionary<string, LinkedListNode<Entry>> _lru = new(StringComparer.OrdinalIgnoreCase);
    private readonly LinkedList<Entry> _order = new();
    private long _windowStart;                 // TickCount64 / ExtractWindowTicks
    private int _extractsThisWindow;
    private bool _dirReady;
    private bool _disposed;

    private sealed class Entry
    {
        public string Key = "";                // 原始路径（LRU 淘汰时反向删字典用）
        public Image? Icon;                    // null + Miss=true = 负缓存
        public bool Miss;
    }

    public IconCache()
    {
        _dir = Path.Combine(AppConfig.Dir, "icon-cache");
    }

    /// <summary>取某路径的应用图标；无路径 / 负缓存 / 本窗口抽取预算用完时返回 null（调用方回退字母圆点）。
    /// 预算只限制 shell 现抽 —— 磁盘命中随时可读，预算耗尽时返回 null 且不写缓存（下一帧再抽）。</summary>
    public Image? Get(string? path)
    {
        if (_disposed || string.IsNullOrWhiteSpace(path)) return null;

        if (_lru.TryGetValue(path, out var node))
        {
            _order.Remove(node);
            _order.AddFirst(node);
            var e0 = node.Value;
            return e0.Miss ? null : e0.Icon;
        }

        // ---- 磁盘层（懒初始化目录 + 超限清理）----
        string? mtime = null;
        try { mtime = File.GetLastWriteTimeUtc(path).Ticks.ToString(CultureInfo.InvariantCulture); }
        catch { }

        string? baseName = null;
        if (mtime != null)
        {
            baseName = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(path.ToLowerInvariant()))) + "-" + mtime;
            if (!_dirReady)
            {
                try
                {
                    Directory.CreateDirectory(_dir);
                    SweepDisk();
                    _dirReady = true;
                }
                catch { }
            }
            if (_dirReady)
            {
                string png = Path.Combine(_dir, baseName + ".png");
                string miss = Path.Combine(_dir, baseName + ".miss");
                if (File.Exists(png))
                {
                    try
                    {
                        using var fs = new FileStream(png, FileMode.Open, FileAccess.Read);
                        // GDI+ 的 Image 在流关闭后不保证仍可绘制：先复制到独立 Bitmap 再关流。
                        using var loaded = Image.FromStream(fs);
                        return Cache(path, new Bitmap(loaded), miss: false);
                    }
                    catch { /* PNG 损坏：删掉重抽 */ try { File.Delete(png); } catch { } }
                }
                if (File.Exists(miss))
                    return Cache(path, null, miss: true);
            }
        }

        // ---- 现抽（限速：预算内才碰 shell）----
        long win = Environment.TickCount64 / ExtractWindowTicks;
        if (win != _windowStart)
        {
            _windowStart = win;
            _extractsThisWindow = 0;
        }
        if (_extractsThisWindow >= MaxExtractsPerWindow)
            return null;                       // 不缓存，下一帧再来

        _extractsThisWindow++;
        Image? bmp = Extract(path);
        if (bmp != null)
        {
            if (baseName != null && _dirReady)
                try { bmp.Save(Path.Combine(_dir, baseName + ".png"), System.Drawing.Imaging.ImageFormat.Png); }
                catch { }
            return Cache(path, bmp, miss: false);
        }

        if (baseName != null && _dirReady)
            try { File.WriteAllBytes(Path.Combine(_dir, baseName + ".miss"), []); }
            catch { }
        return Cache(path, null, miss: true);
    }

    private Image? Cache(string path, Image? icon, bool miss)
    {
        if (_disposed)
        {
            icon?.Dispose();
            return null;
        }
        if (_lru.ContainsKey(path))
        {
            // 同帧重复到达（首个还在磁盘读时不可能，防御性兜底）
            icon?.Dispose();
            var n0 = _lru[path];
            _order.Remove(n0);
            _order.AddFirst(n0);
            return n0.Value.Miss ? null : n0.Value.Icon;
        }
        var n = _order.AddFirst(new Entry { Key = path, Icon = icon, Miss = miss });
        _lru[path] = n;
        TrimLru();
        return miss ? null : icon;
    }

    /// <summary>SHDefExtractIcon 48px → 32 位 ARGB 位图。DrawIcon 到透明 Bitmap（不赌 Icon.ToBitmap 的 alpha 行为）。</summary>
    private static Image? Extract(string path)
    {
        try
        {
            int r = Native.SHDefExtractIcon(path, 0, 0, out IntPtr hIcon, IntPtr.Zero, ((uint)IconPx << 16) | 16u);
            if ((r != 0 && r != 1) || hIcon == IntPtr.Zero) return null;   // 1 = S_FALSE，尺寸不匹配仍可用
            try
            {
                var bmp = new Bitmap(IconPx, IconPx, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(bmp))
                using (var ic = System.Drawing.Icon.FromHandle(hIcon))
                {
                    g.Clear(Color.Transparent);
                    g.DrawIcon(ic, new Rectangle(0, 0, IconPx, IconPx));
                }
                return bmp;
            }
            finally { Native.DestroyIcon(hIcon); }
        }
        catch { return null; }
    }

    /// <summary>目录超限时按修改时间删最旧（png 与 miss 一起算），避免 exe 更新产生的孤儿文件无限累积。</summary>
    private void SweepDisk()
    {
        try
        {
            var files = new DirectoryInfo(_dir).GetFiles();
            if (files.Length <= MaxFiles) return;
            Array.Sort(files, (a, b) => a.LastWriteTimeUtc.CompareTo(b.LastWriteTimeUtc));
            for (int i = 0; i < files.Length - MaxFiles; i++)
                try { files[i].Delete(); }
                catch { }
        }
        catch { }
    }

    private void TrimLru()
    {
        while (_order.Count > LruCapacity)
        {
            var last = _order.Last!;
            _order.RemoveLast();
            _lru.Remove(last.Value.Key);
            last.Value.Icon?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var e in _order)
            e.Icon?.Dispose();
        _order.Clear();
        _lru.Clear();
    }
}
