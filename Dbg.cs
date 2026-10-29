using System.Text;

namespace WMonitor;

internal static class Dbg
{
    private static readonly List<string> Pending = new();
    private static long _pn;

    public static void Prof(string key, double ms)
    {
        lock (Gate)
        {
            Pending.Add($"{key}={ms:F2}");
            if (Pending.Count < 40) return;
            string line = "perf " + string.Join(" ", Pending);
            Pending.Clear();
            Directory.CreateDirectory(AppConfig.Dir);
            File.AppendAllText(Path.Combine(AppConfig.Dir, "debug.log"),
                DateTime.Now.ToString("HH:mm:ss.fff") + " " + line + Environment.NewLine);
        }
        _pn++;
    }

    private static readonly object Gate = new();
    private static long _n;

    public static void Log(string msg)
    {
        if (Interlocked.Increment(ref _n) % 3 != 0) return;
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(AppConfig.Dir);
                File.AppendAllText(Path.Combine(AppConfig.Dir, "debug.log"),
                    DateTime.Now.ToString("HH:mm:ss.fff") + " " + msg + Environment.NewLine);
            }
        }
        catch { }
    }
}
