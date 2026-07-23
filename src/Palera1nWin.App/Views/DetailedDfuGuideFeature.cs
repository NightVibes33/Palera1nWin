using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DarkSwordRestore.Core;
using Palera1nWin.App.Controls;
using Palera1nWin.App.Services;
using Palera1nWin.App.ViewModels;

namespace Palera1nWin.App.Views;

internal static class DetailedDfuGuideFeature
{
    [ModuleInitializer]
    internal static void Register()
    {
        EventManager.RegisterClassHandler(
            typeof(JailbreakView),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ((JailbreakView)sender).EnsureDetailedDfuGuide()));
        EventManager.RegisterClassHandler(
            typeof(DowngradeView),
            FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => ((DowngradeView)sender).EnsureDetailedDfuGuide()));
    }
}

internal static class JailbreakDfuVisualCoordinator
{
    private static WeakReference<JailbreakView>? _activeView;

    public static void Register(JailbreakView view) => _activeView = new WeakReference<JailbreakView>(view);

    public static void Unregister(JailbreakView view)
    {
        if (_activeView?.TryGetTarget(out var active) == true && ReferenceEquals(active, view))
            _activeView = null;
    }

    public static Task<bool?> BeginFromNativePromptAsync(CancellationToken cancellationToken)
    {
        if (_activeView?.TryGetTarget(out var view) != true || !view.IsLoaded)
            return Task.FromResult<bool?>(null);

        var dispatcher = view.Dispatcher;
        if (dispatcher.CheckAccess()) return view.BeginNativePromptDfuGuideAsync(cancellationToken);
        return dispatcher.InvokeAsync(
            () => view.BeginNativePromptDfuGuideAsync(cancellationToken),
            DispatcherPriority.Send,
            cancellationToken).Task.Unwrap();
    }
}

public partial class JailbreakView
{
    private DfuGuideOverlay? _detailedJailbreakDfuOverlay;
    private CancellationTokenSource? _detailedJailbreakDfuCts;
    private bool _detailedJailbreakGuideInitialized;

    internal void EnsureDetailedDfuGuide()
    {
        if (_detailedJailbreakGuideInitialized) return;
        _detailedJailbreakGuideInitialized = true;

        _detailedJailbreakDfuOverlay = new DfuGuideOverlay();
        _detailedJailbreakDfuOverlay.CancelRequested += DetailedJailbreakDfuOverlay_CancelRequested;
        CorrectNativeTimingCopy(_detailedJailbreakDfuOverlay);
        WrapContentWithOverlay(_detailedJailbreakDfuOverlay);
        JailbreakDfuVisualCoordinator.Register(this);
        Unloaded += DetailedJailbreakView_Unloaded;
    }

    internal async Task<bool?> BeginNativePromptDfuGuideAsync(CancellationToken cancellationToken)
    {
        if (_detailedJailbreakDfuOverlay is null || DataContext is not JailbreakViewModel viewModel)
            return null;
        if (IsDfuOrPongo(viewModel.DeviceModeLabel)) return true;

        _detailedJailbreakDfuCts?.Cancel();
        _detailedJailbreakDfuCts?.Dispose();
        _detailedJailbreakDfuCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var ownedCts = _detailedJailbreakDfuCts;
        var token = ownedCts.Token;
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // This project is currently physically targeting iPad6,11/iPad6,12, which use
        // the Home + Top/Power path in palera1n's native dfuhelper.
        var profile = DfuGuideButtonProfile.Home;
        _detailedJailbreakDfuOverlay.Open(profile);
        _ = RunNativePromptSequenceAsync(viewModel, profile, ready, ownedCts);

        using var registration = cancellationToken.Register(() =>
        {
            ownedCts.Cancel();
            ready.TrySetResult(false);
        });
        return await ready.Task.ConfigureAwait(true);
    }

    private async Task RunNativePromptSequenceAsync(
        JailbreakViewModel viewModel,
        DfuGuideButtonProfile profile,
        TaskCompletionSource<bool> ready,
        CancellationTokenSource ownedCts)
    {
        try
        {
            var detected = await DfuGuideSequence.RunAsync(
                profile,
                () => IsDfuOrPongo(viewModel.DeviceModeLabel),
                frame => _detailedJailbreakDfuOverlay?.Apply(frame),
                ownedCts.Token,
                holdSequenceStarting: () => ready.TrySetResult(true));

            if (!ready.Task.IsCompleted) ready.TrySetResult(detected);
            if (detected)
            {
                await Task.Delay(650, ownedCts.Token);
                _detailedJailbreakDfuOverlay?.Close();
            }
        }
        catch (OperationCanceledException)
        {
            ready.TrySetResult(false);
            _detailedJailbreakDfuOverlay?.Apply(new DfuGuideFrame(
                DfuGuidePhase.Cancelled,
                profile,
                "GUIDE CANCELLED",
                "DFU entry was cancelled",
                "No jailbreak payload was started.",
                "Press Start Jailbreak again when you are ready.",
                null,
                0,
                false,
                false,
                false));
            _detailedJailbreakDfuOverlay?.Close();
        }
        finally
        {
            if (ReferenceEquals(_detailedJailbreakDfuCts, ownedCts))
            {
                _detailedJailbreakDfuCts.Dispose();
                _detailedJailbreakDfuCts = null;
            }
        }
    }

    private void DetailedJailbreakDfuOverlay_CancelRequested(object? sender, EventArgs e) =>
        _detailedJailbreakDfuCts?.Cancel();

    private void DetailedJailbreakView_Unloaded(object sender, RoutedEventArgs e)
    {
        JailbreakDfuVisualCoordinator.Unregister(this);
        _detailedJailbreakDfuCts?.Cancel();
        _detailedJailbreakDfuCts?.Dispose();
        _detailedJailbreakDfuCts = null;
        Unloaded -= DetailedJailbreakView_Unloaded;
    }

    private static bool IsDfuOrPongo(string label) =>
        label.Contains("DFU", StringComparison.OrdinalIgnoreCase) ||
        label.Contains("Pongo", StringComparison.OrdinalIgnoreCase);

    private void WrapContentWithOverlay(DfuGuideOverlay overlay)
    {
        if (Content is not UIElement existing) return;
        Content = null;
        var root = new Grid();
        root.Children.Add(existing);
        root.Children.Add(overlay);
        Content = root;
    }

    private static void CorrectNativeTimingCopy(DependencyObject root)
    {
        if (root is TextBlock { Text: "8-second hold" } text) text.Text = "4-second hold";
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            CorrectNativeTimingCopy(VisualTreeHelper.GetChild(root, index));
    }
}

public partial class DowngradeView
{
    private DfuGuideOverlay? _detailedDowngradeDfuOverlay;
    private bool _detailedDowngradeGuideInitialized;

    internal void EnsureDetailedDfuGuide()
    {
        if (_detailedDowngradeGuideInitialized) return;
        _detailedDowngradeGuideInitialized = true;

        _detailedDowngradeDfuOverlay = new DfuGuideOverlay();
        _detailedDowngradeDfuOverlay.CancelRequested += (_, _) => _dfuGuideCts?.Cancel();
        JailbreakView.CorrectNativeTimingCopy(_detailedDowngradeDfuOverlay);
        WrapDowngradeContentWithOverlay(_detailedDowngradeDfuOverlay);

        StartDfuGuideButton.Click -= StartDfuGuide_Click;
        StartDfuGuideButton.Click += DetailedDowngradeDfuGuide_Click;
    }

    private async void DetailedDowngradeDfuGuide_Click(object sender, RoutedEventArgs e)
    {
        var device = _detectedDarkSwordDevice;
        if (device is null || _detailedDowngradeDfuOverlay is null) return;

        _dfuGuideCts?.Cancel();
        _dfuGuideCts?.Dispose();
        _dfuGuideCts = new CancellationTokenSource();
        var token = _dfuGuideCts.Token;
        var profile = device.DfuProfile == DfuButtonProfile.VolumeDown
            ? DfuGuideButtonProfile.VolumeDown
            : DfuGuideButtonProfile.Home;

        StartDfuGuideButton.IsEnabled = false;
        CancelDfuGuideButton.IsEnabled = true;
        DfuGuideProgress.Value = 0;
        _detailedDowngradeDfuOverlay.Open(profile);

        try
        {
            var detected = await DfuGuideSequence.RunAsync(
                profile,
                IsDarkSwordDfuOrPongo,
                frame =>
                {
                    _detailedDowngradeDfuOverlay.Apply(frame);
                    DfuGuideProgress.Value = frame.Progress;
                    DfuGuideStatusText.Text = frame.SecondsRemaining is int seconds
                        ? $"{frame.Title} — {seconds}"
                        : frame.Title;
                },
                token);

            if (!detected) return;
            SetDfuGuideSuccess();
            await Task.Delay(650, token);
            _detailedDowngradeDfuOverlay.Close();
        }
        catch (OperationCanceledException)
        {
            DfuGuideStatusText.Text = "DFU guide cancelled.";
            DfuGuideProgress.Value = 0;
            _detailedDowngradeDfuOverlay.Close();
        }
        catch (Exception exception)
        {
            DfuGuideStatusText.Text = $"DFU guide stopped: {exception.Message}";
            AppendLog($"Detailed DFU guide failed: {exception}");
        }
        finally
        {
            _dfuGuideCts?.Dispose();
            _dfuGuideCts = null;
            CancelDfuGuideButton.IsEnabled = false;
            StartDfuGuideButton.IsEnabled = _detectedDarkSwordDevice is not null;
        }
    }

    private bool IsDarkSwordDfuOrPongo() =>
        _monitor.Current.Mode is AppleDeviceMode.Dfu or AppleDeviceMode.PwnedDfu or AppleDeviceMode.Pongo;

    private void WrapDowngradeContentWithOverlay(DfuGuideOverlay overlay)
    {
        if (Content is not UIElement existing) return;
        Content = null;
        var root = new Grid();
        root.Children.Add(existing);
        root.Children.Add(overlay);
        Content = root;
    }
}
