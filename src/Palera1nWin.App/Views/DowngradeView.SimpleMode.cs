using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using DarkSwordRestore.Core;
using Microsoft.Win32;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private const string SimpleVisibleTag = "DarkSword.Simple.Visible";
    private bool _simpleModeInitialized;
    private DispatcherTimer? _simpleModeTimer;
    private TextBlock _simpleDeviceText = null!;
    private TextBlock _simpleFirmwareText = null!;
    private TextBlock _simpleStageText = null!;
    private ProgressBar _simpleProgress = null!;
    private Button _simpleTestButton = null!;
    private Button _simpleStartButton = null!;
    private Button _simpleBootButton = null!;
    private Button _simpleImportButton = null!;

    private void InitializeSimpleDowngradeUi()
    {
        if (_simpleModeInitialized) return;
        if (Content is not ScrollViewer scroller || scroller.Content is not StackPanel root) return;
        _simpleModeInitialized = true;

        var section = new TextBlock
        {
            Text = "DARKSWORD QUICK ACTIONS",
            Margin = new Thickness(0, 22, 0, 10),
            Tag = SimpleVisibleTag,
        };
        if (TryFindResource("Text.Section") is Style sectionStyle) section.Style = sectionStyle;

        var card = new Border
        {
            Tag = SimpleVisibleTag,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Background = ResourceBrush("Brush.Card"),
            BorderBrush = ResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1),
        };
        var content = new StackPanel();
        card.Child = content;

        content.Children.Add(new TextBlock
        {
            Text = "Four buttons. The app handles detection and checks automatically.",
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
        content.Children.Add(new TextBlock
        {
            Text = "Start Downgrade selects and inspects the IPSW, identifies the connected device, runs DFU → Pwned/Pongo when needed, verifies the driver and toolchain, and then shows one final erase confirmation.",
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("Brush.TextTertiary"),
        });

        var state = new Border
        {
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(7),
            Background = ResourceBrush("Brush.SurfaceSecondary"),
            BorderBrush = ResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1),
        };
        var stateStack = new StackPanel();
        state.Child = stateStack;
        _simpleDeviceText = SimpleCaption("Device: disconnected");
        _simpleFirmwareText = SimpleCaption("IPSW: selected automatically when Start Downgrade is pressed");
        _simpleStageText = new TextBlock
        {
            Text = "Ready",
            Margin = new Thickness(0, 9, 0, 0),
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };
        _simpleProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            Margin = new Thickness(0, 10, 0, 0),
        };
        _simpleProgress.SetBinding(System.Windows.Controls.Primitives.RangeBase.ValueProperty,
            new Binding(nameof(OperationProgress.Value)) { Source = OperationProgress, Mode = BindingMode.OneWay });
        stateStack.Children.Add(_simpleDeviceText);
        stateStack.Children.Add(_simpleFirmwareText);
        stateStack.Children.Add(_simpleStageText);
        stateStack.Children.Add(_simpleProgress);
        content.Children.Add(state);

        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) };
        _simpleStartButton = SimpleButton("Start Downgrade", SimpleStartDowngrade_Click, primary: true);
        _simpleTestButton = SimpleButton("Test DFU → Pwned/Pongo", SimpleTestHardware_Click);
        _simpleBootButton = SimpleButton("Boot Device", SimpleBootDevice_Click, primary: true);
        _simpleImportButton = SimpleButton("Import Boot Profile", SimpleImportProfile_Click);
        buttons.Children.Add(_simpleStartButton);
        buttons.Children.Add(_simpleTestButton);
        buttons.Children.Add(_simpleBootButton);
        buttons.Children.Add(_simpleImportButton);
        content.Children.Add(buttons);

        content.Children.Add(new TextBlock
        {
            Text = "Start Downgrade is destructive. Boot Device is not destructive and is used after every shutdown, restart, or dead battery.",
            Margin = new Thickness(0, 8, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("Brush.TextTertiary"),
        });

        root.Children.Insert(Math.Min(2, root.Children.Count), section);
        root.Children.Insert(Math.Min(3, root.Children.Count), card);
        ApplySimpleDowngradeLayout();

        _monitor.DeviceChanged += SimpleMode_DeviceChanged;
        IpswPathBox.TextChanged += SimpleMode_FirmwareChanged;
        PtePathBox.TextChanged += SimpleMode_BootProfileChanged;
        _simpleModeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(500),
        };
        _simpleModeTimer.Tick += SimpleModeTimer_Tick;
        _simpleModeTimer.Start();
        Dispatcher.BeginInvoke(ApplySimpleDowngradeLayout, DispatcherPriority.Loaded);
        RefreshSimpleModeState();
    }

    private void ApplySimpleDowngradeLayout()
    {
        if (Content is not ScrollViewer scroller || scroller.Content is not StackPanel root) return;
        for (var index = 0; index < root.Children.Count; index++)
        {
            if (root.Children[index] is not FrameworkElement element) continue;
            var keep = index < 2 || string.Equals(element.Tag as string, SimpleVisibleTag, StringComparison.Ordinal);
            element.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private static TextBlock SimpleCaption(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        Foreground = Brushes.Gray,
    };

    private Button SimpleButton(string text, RoutedEventHandler handler, bool primary = false)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 42,
            MinWidth = 185,
            Padding = new Thickness(18, 8, 18, 8),
            Margin = new Thickness(0, 0, 10, 10),
            FontWeight = FontWeights.SemiBold,
        };
        if (primary)
        {
            button.Background = ResourceBrush("Brush.Accent");
            button.Foreground = Brushes.White;
        }
        button.Click += handler;
        return button;
    }

    private void SimpleMode_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(RefreshSimpleModeState);

    private void SimpleMode_FirmwareChanged(object sender, TextChangedEventArgs e) => RefreshSimpleModeState();
    private void SimpleMode_BootProfileChanged(object sender, TextChangedEventArgs e) => RefreshSimpleModeState();
    private void SimpleModeTimer_Tick(object? sender, EventArgs e) => RefreshSimpleModeState();

    private void RefreshSimpleModeState()
    {
        if (!_simpleModeInitialized) return;
        var snapshot = _monitor.Current;
        var device = snapshot.ProductType ?? DetectedProductType ?? snapshot.DisplayName ?? "disconnected";
        var identity = string.IsNullOrWhiteSpace(snapshot.NormalizedEcid) ? string.Empty : $" • ECID {snapshot.NormalizedEcid}";
        _simpleDeviceText.Text = $"Device: {device} • {snapshot.Mode}{identity}";
        _simpleFirmwareText.Text = File.Exists(IpswPathBox.Text)
            ? $"IPSW: {Path.GetFileName(IpswPathBox.Text)}"
            : "IPSW: selected automatically when Start Downgrade is pressed";
        _simpleStageText.Text = $"{CurrentStageText.Text}: {CurrentDetailText.Text}";

        var hardwareBusy = Shell?.HardwareOperations.Current.IsBusy == true;
        _simpleStartButton.Content = _busy ? "Cancel" : "Start Downgrade";
        _simpleStartButton.IsEnabled = _busy || !hardwareBusy;
        _simpleTestButton.IsEnabled = !_busy && !hardwareBusy;
        _simpleImportButton.IsEnabled = !_busy && !hardwareBusy;
        _simpleBootButton.IsEnabled = !_busy && !hardwareBusy;
    }

    private void SimpleTestHardware_Click(object sender, RoutedEventArgs e) =>
        ValidateExactHardware_Click(sender, e);

    private void SimpleBootDevice_Click(object sender, RoutedEventArgs e) =>
        ValidatedTetherBoot_Click(sender, e);

    private void SimpleImportProfile_Click(object sender, RoutedEventArgs e) =>
        ImportBootProfile_Click(sender, e);

    private async void SimpleStartDowngrade_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            _operationCts?.Cancel();
            return;
        }

        try
        {
            var inspection = await EnsureSimpleIpswAsync();
            if (inspection is null) return;

            var receipt = LoadCurrentExactHardwareValidation();
            if (receipt is null || !inspection.MatchesProductType(receipt.ProductType))
            {
                receipt = await RunSimpleHardwareValidationAsync(inspection);
                if (receipt is null) return;
            }

            SetDetectedDevice(receipt.ProductType, DarkSwordDeviceCatalog.Find(receipt.ProductType), null, clearFirmware: false);
            if (!inspection.MatchesProductType(receipt.ProductType))
            {
                ShowMessage(
                    $"The selected IPSW targets {string.Join(", ", inspection.SupportedProductTypes)}, but DFU reports {receipt.ProductType}.",
                    "Wrong IPSW",
                    MessageBoxImage.Error);
                return;
            }

            _inspection = inspection;
            if (!ConfirmFinalDowngradeSummary()) return;

            _operationCts?.Cancel();
            _operationCts?.Dispose();
            _operationCts = new CancellationTokenSource();
            var lease = await TryAcquireHardwareLeaseAsync(
                HardwareOperationKind.Downgrade,
                "One-button exact-device tethered downgrade",
                _operationCts.Token);
            if (lease is null)
            {
                _operationCts.Dispose();
                _operationCts = null;
                return;
            }

            SetBusy(true, "Start Downgrade", "Enter clean DFU on the validated device. The app will continue automatically.");
            try
            {
                var session = await _orchestrator.RunFullDowngradeAsync(
                    inspection.Path,
                    destructiveOperationConfirmed: true,
                    new Progress<RestoreProgress>(HandleEnhancedProgress),
                    AppendLog,
                    _operationCts.Token,
                    expectedHardwareGateEcid: receipt.Ecid);

                PtePathBox.Text = session.PteBlockPath ?? string.Empty;
                ShowPostDowngradeDashboard(session);
                await SaveCompletedBootProfileAsync(session);
                await RefreshRecoveryStateAsync();
                ShowMessage(
                    $"Downgrade completed.\n\nBoot profile: boot-profile.json\nSession: {session.SessionId}\n\nUse Boot Device after every shutdown or dead battery.",
                    "Downgrade complete",
                    MessageBoxImage.Information);
            }
            catch (OperationCanceledException)
            {
                HandleEnhancedProgress(new RestoreProgress(
                    RestoreStage.Cancelled,
                    OperationProgress.Value,
                    "Cancelled",
                    "The operation stopped. Valid recovery artifacts remain saved."));
            }
            catch (Exception exception)
            {
                var stage = exception is DarkSwordException darkSword ? darkSword.Stage : RestoreStage.Failed;
                HandleEnhancedProgress(new RestoreProgress(RestoreStage.Failed, OperationProgress.Value, "Downgrade stopped", exception.Message));
                AppendLog(exception.ToString());
                var guidance = DowngradeFailureTranslator.Translate(exception.Message, stage);
                ShowMessage(guidance.DisplayText, guidance.Title, MessageBoxImage.Error);
            }
            finally
            {
                await lease.DisposeAsync();
                _operationCts?.Dispose();
                _operationCts = null;
                SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
                await RefreshRecoveryStateAsync();
            }
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Cancelled", "No destructive restore is running.");
        }
        catch (Exception exception)
        {
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Could not start downgrade", MessageBoxImage.Error);
            SetBusy(false, "Ready", "Correct the reported problem and press Start Downgrade again.");
        }
        finally
        {
            RefreshSimpleModeState();
        }
    }

    private async Task<IpswInspectionResult?> EnsureSimpleIpswAsync()
    {
        var path = File.Exists(IpswPathBox.Text) ? IpswPathBox.Text : null;
        if (path is null)
        {
            var dialog = new OpenFileDialog
            {
                Title = "Select the iOS/iPadOS 15 IPSW to downgrade to",
                Filter = "Apple firmware (*.ipsw)|*.ipsw|All files (*.*)|*.*",
                CheckFileExists = true,
            };
            if (dialog.ShowDialog(Window.GetWindow(this)) != true) return null;
            path = dialog.FileName;
            IpswPathBox.Text = path;
        }

        SetBusy(true, "Checking IPSW", "Reading BuildManifest and calculating SHA-256.");
        var inspection = await _inspector.InspectAsync(path);
        _inspection = inspection;
        IpswSummaryText.Text = inspection.IsValid
            ? $"iOS/iPadOS {inspection.ProductVersion} ({inspection.BuildVersion}) • {string.Join(", ", inspection.SupportedProductTypes)} • SHA-256 {inspection.Sha256}"
            : string.Join(Environment.NewLine, inspection.Errors);
        if (!inspection.IsValid)
        {
            SetBusy(false, "IPSW rejected", inspection.Errors.FirstOrDefault() ?? "The selected IPSW is not supported.");
            ShowMessage(IpswSummaryText.Text, "IPSW rejected", MessageBoxImage.Error);
            return null;
        }
        SetBusy(false, "IPSW ready", $"Target {inspection.ProductVersion} ({inspection.BuildVersion}) is ready.");
        return inspection;
    }

    private async Task<ExactHardwareValidation?> RunSimpleHardwareValidationAsync(IpswInspectionResult inspection)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.DriverRepair,
            "Automatic DFU → Pwned/Pongo validation before downgrade",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return null;
        }

        SetBusy(true, "Test DFU → Pwned/Pongo", "Enter clean DFU. This test does not erase firmware.");
        try
        {
            var identity = await _orchestrator.ValidateDfuToPongoAsync(
                new Progress<RestoreProgress>(HandleEnhancedProgress),
                AppendLog,
                _operationCts.Token);
            if (!identity.HasExactIdentity)
                throw new InvalidDataException("DFU reached PongoOS, but ProductType and ECID could not be read.");
            if (!inspection.MatchesProductType(identity.ProductType))
                throw new InvalidDataException(
                    $"DFU reports {identity.ProductType}, but the selected IPSW targets {string.Join(", ", inspection.SupportedProductTypes)}.");

            SetDetectedDevice(identity.ProductType, DarkSwordDeviceCatalog.Find(identity.ProductType), null, clearFirmware: false);
            var receipt = new ExactHardwareValidation(
                ExactValidationSchema,
                identity.ProductType!,
                identity.NormalizedEcid!,
                identity.InstanceId,
                identity.Service,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7));
            await SaveExactHardwareValidationAsync(receipt, _operationCts.Token);
            await SaveHardwareValidationAsync();
            RefreshExactHardwareValidationUi();
            return receipt;
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, "Hardware test complete", "Re-enter clean DFU when Start Downgrade asks for it.");
        }
    }

    private void DisposeSimpleDowngradeUi()
    {
        _simpleModeTimer?.Stop();
        if (_simpleModeTimer is not null) _simpleModeTimer.Tick -= SimpleModeTimer_Tick;
        _simpleModeTimer = null;
        _monitor.DeviceChanged -= SimpleMode_DeviceChanged;
        if (_simpleModeInitialized)
        {
            IpswPathBox.TextChanged -= SimpleMode_FirmwareChanged;
            PtePathBox.TextChanged -= SimpleMode_BootProfileChanged;
        }
    }
}
