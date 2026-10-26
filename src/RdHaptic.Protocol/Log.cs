namespace RdHaptic.Protocol;

/// <summary>Tiny file logger under %LOCALAPPDATA%\RdHaptic. Never throws.</summary>
public static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static bool Console { get; set; }

    public static void Init(string name)
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RdHaptic");
        try { Directory.CreateDirectory(dir); } catch { }
        _path = Path.Combine(dir, name + ".log");
    }

    public static void Info(string message) => Write("INF", message);

    public static void Error(string message, Exception? ex = null) =>
        Write("ERR", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] [{Environment.CurrentManagedThreadId}] {message}";
        if (Console)
            System.Console.WriteLine(line);
        if (_path is null)
            return;
        lock (Gate)
        {
            try
            {
                var fi = new FileInfo(_path);
                if (fi.Exists && fi.Length > MaxBytes)
                    File.Move(_path, _path + ".1", overwrite: true);
                File.AppendAllText(_path, line + Environment.NewLine);
            }
            catch { }
        }
    }
}
