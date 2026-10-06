using System.Windows;
using PoeBuilder.App.Services;

namespace PoeBuilder.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        // The off-screen renderer is a developer switch, not a window: it takes no single-instance lock,
        // so the tree can be rendered while the app itself is open.
        if (TreeRenderHarness.Requested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            _ = RenderTreeAsync(e.Args);
            return;
        }
        // The window self-check is the same kind of switch: it lays every editor out off-screen, which is
        // the only way a XAML mistake can be caught without a human clicking through the app.
        if (WindowSelfCheck.Requested(e.Args))
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            base.OnStartup(e);
            _ = CheckWindowsAsync();
            return;
        }
        _instance = new Mutex(true, @"Local\PoeBuilder.Native.Foundation", out _ownsMutex);
        if (!_ownsMutex)
        {
            ThemedDialog.Show("PoeBuilder Native уже запущен / is already running.", "PoeBuilder", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(); return;
        }
        // Crash diagnostics: every unhandled exception is journalled and shown instead of dying silently.
        DispatcherUnhandledException += (_, args) =>
        {
            ErrorLog.Append(args.Exception, "UI dispatcher");
            ShowCrash(args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ErrorLog.Append(args.ExceptionObject as Exception, args.IsTerminating ? "process-terminating" : "domain");
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ErrorLog.Append(args.Exception, "unobserved task");
            args.SetObserved();
        };
        base.OnStartup(e);
    }
    /// <summary>Runs the off-screen renderer and closes the process with its exit code; failures land in
    /// the journal instead of a dialog, because nobody is watching a hidden window.</summary>
    private async Task RenderTreeAsync(string[] args)
    {
        int code = 1;
        try { code = await TreeRenderHarness.RunAsync(args); }
        catch (Exception e) { ErrorLog.Append(e, "render-tree"); Console.Error.WriteLine(e); }
        Shutdown(code);
    }
    /// <summary>Runs the window self-check and closes the process with its exit code, for the same reason
    /// the renderer does: nobody is watching a hidden window, so the journal and the console carry it.</summary>
    private async Task CheckWindowsAsync()
    {
        int code = 1;
        try { code = await WindowSelfCheck.RunAsync(); }
        catch (Exception e) { ErrorLog.Append(e, "check-windows"); Console.Error.WriteLine(e); }
        Console.Out.Flush(); Console.Error.Flush();
        // Not Shutdown(): a window the check kept open (its draft has unsaved edits) would ask to save it on
        // the way out, and this process has nobody to answer. The journal and the console already carry the
        // result by now.
        Environment.Exit(code);
    }
    private static string? _lastDialogSignature;
    private static DateTime _lastDialogAtUtc;
    private static void ShowCrash(Exception exception)
    {
        // Layout-time exceptions repeat on every layout pass: suppress identical dialogs within 5 s,
        // keep everything in the journal, never block the user with a modal storm.
        var signature = exception.GetType().FullName + " · " + exception.Message;
        if (signature == _lastDialogSignature && (DateTime.UtcNow - _lastDialogAtUtc).TotalSeconds < 5) return;
        _lastDialogSignature = signature; _lastDialogAtUtc = DateTime.UtcNow;
        ThemedDialog.Show(
            "Непредвиденная ошибка. Приложение продолжит работу, но эта операция не выполнена.\n" +
            "Подробности записаны в журнал:\n" + ErrorLog.LogPath + "\n\n" +
            exception.GetType().Name + ": " + exception.Message + "\n\n" +
            "Пришлите, пожалуйста, файл журнала разработчику.\n" +
            "Unexpected error; details were written to the journal file above. Please send it to the developer.",
            "PoeBuilder", MessageBoxButton.OK, MessageBoxImage.Error);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex) _instance?.ReleaseMutex();
        _instance?.Dispose(); base.OnExit(e);
    }
}
