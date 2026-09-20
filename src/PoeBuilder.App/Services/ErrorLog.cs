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

    public static void Append(Exception? exception, string source)
    {
        if (exception is null) return;
        try
        {
            string? dir = Path.GetDirectoryName(LogPath);
            if (dir is not null) Directory.CreateDirectory(dir);
            var line = new StringBuilder()
                .AppendLine("==== " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " · " + source)
                .AppendLine(exception.GetType().FullName + ": " + exception.Message)
                .AppendLine(exception.StackTrace ?? "(no stack trace)")
                .AppendLine()
                .ToString();
            File.AppendAllText(LogPath, line);
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
