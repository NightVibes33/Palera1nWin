using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Palera1nWin.App.ViewModels;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _sharedLoggingWired;
    private string? _lastSharedProgressLine;
    private int _fallbackMirroredLength;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Loaded += SharedLogging_Loaded;
    }

    private void SharedLogging_Loaded(object sender, RoutedEventArgs e)
    {
        EnsureSharedShellDataContext();
        if (_sharedLoggingWired) return;

        _sharedLoggingWired = true;
        OperationProgress.ValueChanged += SharedLogging_ProgressChanged;
        LogBox.TextChanged += SharedLogging_LogBoxChanged;

        ResolveSharedShell()?.AppendLog(
            "darksword",
            "Downgrade log bridge connected. Native restore output and stage changes will appear in the shared Logs tab.");
    }

    private void EnsureSharedShellDataContext()
    {
        if (DataContext is MainViewModel) return;
        if (Window.GetWindow(this)?.DataContext is MainViewModel ownerShell)
        {
            DataContext = ownerShell;
            return;
        }
        if (Application.Current?.MainWindow?.DataContext is MainViewModel appShell)
        {
            DataContext = appShell;
        }
    }

    private MainViewModel? ResolveSharedShell()
    {
        EnsureSharedShellDataContext();
        return DataContext as MainViewModel
               ?? Window.GetWindow(this)?.DataContext as MainViewModel
               ?? Application.Current?.MainWindow?.DataContext as MainViewModel;
    }

    private void SharedLogging_ProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // UpdateProgress changes the ProgressBar before it updates the stage/detail labels.
        // Queue one UI turn so the shared line contains the new stage rather than the old one.
        Dispatcher.BeginInvoke(
            DispatcherPriority.Background,
            new Action(() =>
            {
                if (_disposed) return;
                var stage = CurrentStageText.Text?.Trim();
                var detail = CurrentDetailText.Text?.Trim();
                if (string.IsNullOrWhiteSpace(stage) && string.IsNullOrWhiteSpace(detail)) return;

                var line = string.IsNullOrWhiteSpace(detail) ? stage! : $"{stage}: {detail}";
                if (string.Equals(line, _lastSharedProgressLine, StringComparison.Ordinal)) return;
                _lastSharedProgressLine = line;
                ResolveSharedShell()?.AppendLog("darksword-stage", line);
            }));
    }

    private void SharedLogging_LogBoxChanged(object sender, TextChangedEventArgs e)
    {
        var text = LogBox.Text ?? string.Empty;

        // The normal path already forwards each AppendLog call through Shell.AppendLog.
        // This fallback only mirrors text when the navigation host failed to supply the shell
        // DataContext, which was the reason downgrade logs could disappear from the Logs tab.
        if (Shell is not null)
        {
            _fallbackMirroredLength = text.Length;
            return;
        }

        var shell = ResolveSharedShell();
        if (shell is null) return;
        if (_fallbackMirroredLength < 0 || _fallbackMirroredLength > text.Length)
            _fallbackMirroredLength = 0;

        var added = text[_fallbackMirroredLength..];
        _fallbackMirroredLength = text.Length;
        foreach (var raw in added.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            shell.AppendLog(
                "darksword",
                line,
                line.Contains("error", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("failed", StringComparison.OrdinalIgnoreCase));
        }
    }
}
