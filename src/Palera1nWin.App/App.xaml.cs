using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using Palera1nWin.App.ViewModels;
using Palera1nWin.App.Views;
using Palera1nWin.Core.Security;
using Palera1nWin.Core.Settings;
using Palera1nWin.Core.Util;
using Wpf.Ui.Appearance;

namespace Palera1nWin.App;

public partial class App : Application
{
    private Mutex? _singleInstanceMutex;
    private bool _ownsSingleInstanceMutex;
    private MainViewModel? _mainViewModel;
    private int _fatalShutdownStarted;

    private async void OnStartup(object sender, StartupEventArgs e)
    {
        var selfTest = e.Args.Any(argument =>
            string.Equals(argument, "--self-test", StringComparison.OrdinalIgnoreCase));
        var downgradeUiSelfTest = e.Args.Any(argument =>
            string.Equals(argument, "--downgrade-ui-self-test", StringComparison.OrdinalIgnoreCase));
        var anySelfTest = selfTest || downgradeUiSelfTest;

        PackageIntegrityReport integrity;
        try
        {
            integrity = await new PackageIntegrityVerifier().VerifyAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            if (!anySelfTest)
            {
                MessageBox.Show(
                    $"Package integrity verification failed before startup:\n\n{exception.Message}",
                    "Palera1nWin package blocked",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown(91);
            return;
        }

        if (!integrity.IsValid)
        {
            WriteIntegrityFailure(integrity);
            if (!anySelfTest)
            {
                MessageBox.Show(
                    "Palera1nWin refused to start because packaged files were changed, removed, or added outside the tested release.\n\n" +
                    integrity.Summary +
                    "\n\nDelete this folder and extract a fresh verified release ZIP.",
                    "Palera1nWin package blocked",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
            Shutdown(92);
            return;
        }

        if (selfTest)
        {
            var settings = AppSettings.Load();
            var toolchain = Paths.ResolveToolchainRoot(settings.ToolchainRoot);
            IReadOnlyList<string> missing = [];
            var validToolchain = toolchain is not null && Paths.ValidateToolchain(toolchain, out missing);
            var resultPath = Path.Combine(AppContext.BaseDirectory, "self-test-result.txt");
            try
            {
                File.WriteAllText(
                    resultPath,
                    integrity.Summary + Environment.NewLine +
                    (validToolchain
                        ? "Toolchain validation passed."
                        : $"Toolchain validation failed: {string.Join(", ", missing)}"));
            }
            catch { }
            Shutdown(validToolchain ? 0 : 93);
            return;
        }

        if (downgradeUiSelfTest)
        {
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var exitCode = await RunDowngradeUiSelfTestAsync().ConfigureAwait(true);
            Shutdown(exitCode);
            return;
        }

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

    private async Task<int> RunDowngradeUiSelfTestAsync()
    {
        var resultPath = Path.Combine(AppContext.BaseDirectory, "downgrade-ui-self-test-result.txt");
        Exception? dispatcherFailure = null;
        DispatcherUnhandledExceptionEventHandler handler = (_, args) =>
        {
            dispatcherFailure = args.Exception;
            args.Handled = true;
        };
        DispatcherUnhandledException += handler;

        DowngradeView? view = null;
        Window? host = null;
        try
        {
            ApplicationAccentColorManager.Apply(
                System.Windows.Media.Color.FromRgb(0x2D, 0xD4, 0xBF),
                ApplicationTheme.Dark);

            view = new DowngradeView();
            host = new Window
            {
                Content = view,
                Width = 1100,
                Height = 800,
                Left = -20000,
                Top = -20000,
                ShowActivated = false,
                ShowInTaskbar = false,
                WindowStyle = WindowStyle.None,
            };
            host.Show();

            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);
            await Task.Delay(TimeSpan.FromSeconds(2.3)).ConfigureAwait(true);
            await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ContextIdle);

            if (dispatcherFailure is not null) throw new InvalidOperationException(
                "The Downgrade tab raised an unhandled dispatcher exception during Loaded/log/timer processing.",
                dispatcherFailure);

            foreach (var fieldName in new[] { "_compatibility", "_failure", "_nextAction", "_exportButton" })
            {
                var field = typeof(DowngradeView).GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic);
                if (field?.GetValue(view) is null)
                    throw new InvalidOperationException($"Downgrade operational control {fieldName} was not initialized.");
            }

            File.WriteAllText(resultPath, "PASS: Downgrade tab Loaded, log, timer, and operational dashboard initialization completed.");
            return 0;
        }
        catch (Exception exception)
        {
            WriteCrashLog(exception);
            try { File.WriteAllText(resultPath, "FAIL: " + exception); } catch { }
            return 94;
        }
        finally
        {
            DispatcherUnhandledException -= handler;
            try { host?.Close(); } catch { }
            try { view?.Dispose(); } catch { }
        }
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        WriteCrashLog(e.Exception);
        e.Handled = true;
        if (Interlocked.Exchange(ref _fatalShutdownStarted, 1) != 0) return;

        var operation = _mainViewModel?.ActiveHardwareOperation;
        var operationText = operation?.IsBusy == true
            ? $"\n\nActive operation: {operation.Operation}. The app will close rather than continue with unknown USB/process state. Keep the device connected until Windows finishes re-enumerating it, then reopen the app and use Recovery."
            : "\n\nThe app will close rather than continue in a potentially inconsistent state.";
        MessageBox.Show(
            $"Palera1nWin hit an unexpected error and wrote a crash log to:\n{AppSettings.LogsDirectory}\n\n{e.Exception.Message}{operationText}",
            "Palera1nWin fatal error",
            MessageBoxButton.OK,
            MessageBoxImage.Error);

        Dispatcher.BeginInvoke(DispatcherPriority.Send, new Action(() =>
        {
            try { _mainViewModel?.Dispose(); } catch { }
            _mainViewModel = null;
            Shutdown(-1);
        }));
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception) WriteCrashLog(exception);
    }

    private static void WriteIntegrityFailure(PackageIntegrityReport report)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            File.WriteAllText(
                Path.Combine(AppSettings.LogsDirectory, $"integrity-{DateTime.Now:yyyyMMdd-HHmmss}.log"),
                report.Summary);
        }
        catch { }
    }

    private static void WriteCrashLog(Exception exception)
    {
        try
        {
            Directory.CreateDirectory(AppSettings.LogsDirectory);
            string path = Path.Combine(AppSettings.LogsDirectory, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}.log");
            File.WriteAllText(path, exception.ToString());
        }
        catch
        {
            // Crash logging is best-effort.
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _mainViewModel?.Dispose(); } catch { }
        _mainViewModel = null;

        if (_ownsSingleInstanceMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); } catch { }
        }

        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }
}
