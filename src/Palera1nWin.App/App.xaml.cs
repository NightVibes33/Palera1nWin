using System.IO;
using System.Windows;
using System.Windows.Threading;
using Palera1nWin.App.ViewModels;
using Palera1nWin.Core.Settings;
using Wpf.Ui.Appearance;

namespace Palera1nWin.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private MainViewModel? _mainViewModel;

    private void OnStartup(object sender, StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, "Palera1nWin.App.SingleInstance", out bool createdNew);
        _ownsSingleInstanceMutex = createdNew;
        if (!createdNew)
        {
            MessageBox.Show("Palera1nWin is already running.", "Palera1nWin", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteCrashLog(args.Exception);
            args.SetObserved();
        };

        ApplicationAccentColorManager.Apply(
            System.Windows.Media.Color.FromRgb(0x2D, 0xD4, 0xBF),
            ApplicationTheme.Dark);

        int? initialTab = null;
        for (int i = 0; i < e.Args.Length; i++)
        {
            if (string.Equals(e.Args[i], "--tab", StringComparison.OrdinalIgnoreCase) &&
                i + 1 < e.Args.Length && int.TryParse(e.Args[i + 1], out int tab))
            {
                initialTab = tab;
            }
        }

        _mainViewModel = new MainViewModel();
        var window = new MainWindow(_mainViewModel, initialTab);
        MainWindow = window;
        window.Show();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        MessageBox.Show(
            $"Palera1nWin hit an unexpected error and wrote a crash log to:\n{AppSettings.LogsDirectory}\n\n{e.Exception.Message}",
            "Palera1nWin",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            WriteCrashLog(exception);
        }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            string path = Path.Combine(AppSettings.LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
        }
        catch (Exception)
        {
            // Crash logging is best-effort.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _mainViewModel?.Dispose();

        if (_ownsSingleInstanceMutex)
        {
            _singleInstanceMutex?.ReleaseMutex();
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
