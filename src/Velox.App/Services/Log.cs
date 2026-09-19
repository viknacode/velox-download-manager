using System.IO;

namespace Velox.App.Services;

/// <summary>Log simples em %LOCALAPPDATA%\VeloxDM\velox.log (rotaciona em ~1 MB).</summary>
public static class Log
{
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string dataDirectory)
    {
        _path = Path.Combine(dataDirectory, "velox.log");
        try
        {
            if (File.Exists(_path) && new FileInfo(_path).Length > 1_000_000)
                File.Move(_path, Path.ChangeExtension(_path, ".old.log"), overwrite: true);
        }
        catch { }
    }

    public static void Info(string message) => Write("INFO", message);
    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex == null ? message : $"{message}\n{ex}");

    private static void Write(string level, string message)
    {
        if (_path == null) return;
        try
        {
            lock (Gate)
                File.AppendAllText(_path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}{Environment.NewLine}");
        }
        catch { }
    }
}
