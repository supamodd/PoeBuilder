using System.IO;
using System.Text;

namespace PoeBuilder.App.Services;

/// <summary>
/// Append-only crash journal: every unhandled exception (UI dispatcher, domain, unobserved tasks)
/// lands in %LOCALAPPDATA%\PoeBuilder\Native\error.log. Diagnostics only; never throws.
/// </summary>
public static class ErrorLog
{
    public static string LogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PoeBuilder", "Native", "error.log");

    // Repeating layout exceptions can fire hundreds of times per second (a real 12 MB log was
    // produced this way). Same source + same first stack line is counted, not re-written.
    private static readonly object Gate = new();
    private static string? _lastKey;
    private static int _repeat;

    public static void Append(Exception? exception, string source)
    {
        if (exception is null) return;
        try
        {
            string head = exception.StackTrace?.Split('\n').FirstOrDefault()?.Trim() ?? "(no stack trace)";
            string key = source + "\n" + exception.GetType().FullName + "\n" + head;
            string block = new StringBuilder()
                .AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + source)
                .AppendLine(exception.GetType().FullName + ": " + exception.Message)
                .AppendLine(exception.StackTrace ?? "(no stack trace)")
                .AppendLine()
                .ToString();
            lock (Gate)
            {
                if (key == _lastKey)
                {
                    _repeat++;
                    if (_repeat is 10 or 100 or 1000 or 10000)
                        File.AppendAllText(LogPath, $"==== {DateTime.Now:yyyy-MM-dd HH:mm:ss} · {source}\n(тот же повторился ещё раз; всего подряд: {_repeat + 1})\n\n");
                    return;
                }
                if (_repeat > 0) File.AppendAllText(LogPath, $"(предыдущая ошибка повторилась {_repeat} раз подряд)\n\n");
                _lastKey = key; _repeat = 0;
                string? dir = Path.GetDirectoryName(LogPath);
                if (dir is not null) Directory.CreateDirectory(dir);
                File.AppendAllText(LogPath, block);
            }
        }
        catch { /* diagnostics must never crash the app */ }
    }

    /// <summary>Milestone marker, to correlate a crash with the operation that preceded it.</summary>
    public static void Mark(string message)
    {
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            File.AppendAllText(LogPath, "---- " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + message + "\n");
        }
        catch { /* diagnostics must never crash the app */ }
    }
}
