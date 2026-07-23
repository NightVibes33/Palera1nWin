using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
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

public partial class JailbreakView
{
    private DfuGuideOverlay? _detailedJailbreakDfuOverlay;
    private ButtonBase? _detailedJailbreakStartButton;
    private ICommand? _detailedJailbreakStartCommand;
    private CancellationTokenSource? _detailedJailbreakDfuCts;
    private bool _detailedJailbreakGuideInitialized;

    internal void EnsureDetailedDfuGuide()
    {
        if (_detailedJailbreakGuideInitialized) return;
        _detailedJailbreakGuideInitialized = true;

        _detailedJailbreakDfuOverlay = new DfuGuideOverlay();
        _detailedJailbreakDfuOverlay.CancelRequested += DetailedJailbreakDfuOverlay_CancelRequested;
        WrapContentWithOverlay(_detailedJailbreakDfuOverlay);

        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _detailedJailbreakStartButton = FindButtons(this)
                    .FirstOrDefault(button =>
                        string.Equals(button.Content?.ToString(), "Start Jailbreak", StringComparison.Ordinal));
                if (_detailedJailbreakStartButton is null) return;

                _detailedJailbreakStartCommand = _detailedJailbreakStartButton.Command;
                _detailedJailbreakStartButton.Command = null;
                _detailedJailbreakStartButton.Click += DetailedJailbreakStartButton_Click;
                _detailedJailbreakStartButton.SetBinding(
                    UIElement.IsEnabledProperty,
                    new Binding(nameof(JailbreakViewModel.CanStartJailbreak)));
            }));
    }

    private async void DetailedJailbreakStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_detailedJailbreakDfuCts is not null ||
            _detailedJailbreakStartButton is null ||
            _detailedJailbreakStartCommand is null ||
            _detailedJailbreakStartButton.DataContext is not JailbreakViewModel viewModel)
        {
            return;
        }

        if (!_detailedJailbreakStartCommand.CanExecute(null)) return;
        if (IsDfuOrPongo(viewModel.DeviceModeLabel))
        {
            _detailedJailbreakStartCommand.Execute(null);
            return;
        }

        _detailedJailbreakDfuCts = new CancellationTokenSource();
        _detailedJailbreakStartButton.IsEnabled = false;
        var token = _detailedJailbreakDfuCts.Token;
        var profile = DfuGuideButtonProfile.Home;
        _detailedJailbreakDfuOverlay!.Open(profile);

        try
        {
            var detected = await DfuGuideSequence.RunAsync(
                profile,
                () => IsDfuOrPongo(viewModel.DeviceModeLabel),
                frame => _detailedJailbreakDfuOverlay.Apply(frame),
                token);

            if (!detected) return;
            await Task.Delay(650, token);
            _detailedJailbreakDfuOverlay.Close();
            if (_detailedJailbreakStartCommand.CanExecute(null))
            {
                _detailedJailbreakStartCommand.Execute(null);
            }
        }
        catch (OperationCanceledException)
        {
            _detailedJailbreakDfuOverlay.Apply(new DfuGuideFrame(
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
            _detailedJailbreakDfuOverlay.Close();
        }
        finally
        {
            _detailedJailbreakDfuCts?.Dispose();
            _detailedJailbreakDfuCts = null;
            _detailedJailbreakStartButton.GetBindingExpression(UIElement.IsEnabledProperty)?.UpdateTarget();
        }
    }

    private void DetailedJailbreakDfuOverlay_CancelRequested(object? sender, EventArgs e)
    {
        _detailedJailbreakDfuCts?.Cancel();
        _detailedJailbreakDfuOverlay?.Close();
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

    private static IEnumerable<ButtonBase> FindButtons(DependencyObject root)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
        {
            var child = VisualTreeHelper.GetChild(root, index);
            if (child is ButtonBase button) yield return button;
            foreach (var nested in FindButtons(child)) yield return nested;
        }
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
