using System.Windows;
using PoeBuilder.App.Services;

namespace PoeBuilder.App;

public partial class App : Application
{
    private Mutex? _instance;
    private bool _ownsMutex;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, @"Local\PoeBuilder.Native.Foundation", out _ownsMutex);
        if (!_ownsMutex)
        {
            MessageBox.Show("PoeBuilder Native уже запущен / is already running.", "PoeBuilder", MessageBoxButton.OK, MessageBoxImage.Information);
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
    private static string? _lastDialogSignature;
    private static DateTime _lastDialogAtUtc;
    private static void ShowCrash(Exception exception)
    {
        // Layout-time exceptions repeat on every layout pass: suppress identical dialogs within 5 s,
        // keep everything in the journal, never block the user with a modal storm.
        var signature = exception.GetType().FullName + " · " + exception.Message;
        if (signature == _lastDialogSignature && (DateTime.UtcNow - _lastDialogAtUtc).TotalSeconds < 5) return;
        _lastDialogSignature = signature; _lastDialogAtUtc = DateTime.UtcNow;
        MessageBox.Show(
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
