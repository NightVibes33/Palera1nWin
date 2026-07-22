using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using DarkSwordRestore.Core;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private readonly ObservableCollection<PreflightCheckViewModel> _preflightChecks = [];
    private readonly ObservableCollection<StageTimelineItem> _stageTimeline = [];
    private DowngradePreflightService? _preflightService;
    private DowngradeRecoveryService? _recoveryService;
    private CancellationTokenSource? _preflightCts;
    private CancellationTokenSource? _postVerifyCts;
    private PreflightReport? _lastPreflight;
    private RecoveryCandidate? _recoveryCandidate;
    private RestoreSession? _completedSession;
    private AppleDeviceSnapshot _experienceSnapshot = AppleDeviceSnapshot.Disconnected;
    private bool _experienceInitialized;

    private void InitializeDowngradeExperience()
    {
        if (_experienceInitialized) return;
        _experienceInitialized = true;

        _preflightService = new DowngradePreflightService(_tools, _monitor, _inspector, _driver);
        _recoveryService = new DowngradeRecoveryService(_tools, _runner, _monitor, _sessions, _driver);
        PreflightChecksList.ItemsSource = _preflightChecks;
        RestoreStageList.ItemsSource = _stageTimeline;
        BuildStageTimeline();
        _firmwareItems.CollectionChanged += FirmwareItems_CollectionChanged;
        _ = RefreshRecoveryStateAsync();
    }

    private void FirmwareItems_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        Dispatcher.BeginInvoke(() =>
        {
            if (FirmwareList.SelectedItem is null && _firmwareItems.Count > 0)
            {
                FirmwareList.SelectedItem = _firmwareItems[0];
                FirmwareList.ScrollIntoView(_firmwareItems[0]);
            }

            if (_firmwareItems.Count > 0)
            {
                var recommended = _firmwareItems[0];
                RecommendedFirmwareText.Text =
                    $"Recommended: {recommended.Version} ({recommended.BuildId}) • {recommended.SigningStatus} • {recommended.SizeText}. " +
                    "This is the newest exact-device iOS/iPadOS 15 entry returned by the firmware catalog.";
            }
            else
            {
                RecommendedFirmwareText.Text = "A recommended exact-device firmware appears after the catalog loads.";
            }
        });
    }

    private void UpdateExperienceDeviceState(AppleDeviceSnapshot snapshot)
    {
        _experienceSnapshot = snapshot;
        DfuLiveModeText.Text = snapshot.Mode switch
        {
            AppleDeviceMode.Dfu => "DFU DETECTED — SCREEN MUST BE BLACK",
            AppleDeviceMode.Recovery => "RECOVERY MODE DETECTED — RETRY THE BUTTON TIMING",
            AppleDeviceMode.Pongo => "PONGOOS DETECTED",
            AppleDeviceMode.Normal => "NORMAL MODE — READY TO PREPARE",
            AppleDeviceMode.Disconnected => "DEVICE DISCONNECTED",
            _ => snapshot.Mode.ToString().ToUpperInvariant()
        };
        DfuLiveModeText.Foreground = snapshot.Mode switch
        {
            AppleDeviceMode.Dfu or AppleDeviceMode.Pongo => ResourceBrush("Brush.Success"),
            AppleDeviceMode.Recovery => ResourceBrush("Brush.Danger"),
            _ => ResourceBrush("Brush.TextTertiary")
        };

        var device = _detectedDarkSwordDevice;
        DfuVisualPrimaryText.Text = device?.DfuProfile == DfuButtonProfile.VolumeDown
            ? "SIDE + VOLUME DOWN"
            : "TOP/SIDE + HOME";
        DfuVisualSecondaryText.Text = device?.DfuProfile == DfuButtonProfile.VolumeDown
            ? "After 8 seconds: release Side, keep holding Volume Down"
            : "After 8 seconds: release Top/Side, keep holding Home";

        if (_lastPreflight is not null && !IsPreflightCurrent())
        {
            InvalidatePreflight("Device mode, driver, identity, or firmware changed.");
        }
        UpdateActionState();
    }

    private async void RunPreflight_Click(object sender, RoutedEventArgs e) =>
        await RunPreflightAsync(showResultDialog: true);

    private async Task<bool> RunPreflightAsync(bool showResultDialog)
    {
        if (_preflightService is null) return false;
        if (!File.Exists(IpswPathBox.Text))
        {
            ShowMessage(
                "Select an iOS/iPadOS 15 IPSW first.",
                "Preflight not ready",
                MessageBoxImage.Information);
            return false;
        }

        if (_inspection is null || !string.Equals(_inspection.Path, IpswPathBox.Text, StringComparison.OrdinalIgnoreCase))
        {
            PreflightStatusText.Text = "Inspecting the selected IPSW before preflight...";
            PreflightStatusText.Foreground = ResourceBrush("Brush.Accent");
            _inspection = await _inspector.InspectAsync(IpswPathBox.Text);
            IpswSummaryText.Text = _inspection.IsValid
                ? $"IPSW inspected: iOS/iPadOS {_inspection.ProductVersion} ({_inspection.BuildVersion}) for {string.Join(", ", _inspection.SupportedProductTypes)}."
                : string.Join(Environment.NewLine, _inspection.Errors);
        }

        var productType = DetectedProductType;
        if (string.IsNullOrWhiteSpace(productType))
        {
            ShowMessage(
                "The app could not determine a single exact target. In DFU, use an inspected IPSW that contains exactly one supported ProductType, or reconnect in normal mode and trust this PC.",
                "Preflight not ready",
                MessageBoxImage.Information);
            return false;
        }

        if (_firmwareIdentifier is null)
        {
            var device = DarkSwordDeviceCatalog.Find(productType);
            SetDetectedDevice(productType, device, "Exact ProductType inferred from the inspected IPSW because the device is currently in DFU.", clearFirmware: false);
        }

        _preflightCts?.Cancel();
        _preflightCts?.Dispose();
        _preflightCts = new CancellationTokenSource();
        RunPreflightButton.IsEnabled = false;
        PreflightStatusText.Text = "Running all safety, driver, firmware, storage, battery, and connectivity checks...";
        PreflightStatusText.Foreground = ResourceBrush("Brush.Accent");
        _preflightChecks.Clear();

        try
        {
            var report = await _preflightService.RunAsync(
                IpswPathBox.Text,
                productType,
                repairDfuDriver: true,
                AppendLog,
                _preflightCts.Token);
            _lastPreflight = report;
            _experienceSnapshot = report.Device;
            if (report.Ipsw is not null) _inspection = report.Ipsw;

            foreach (var check in report.Checks)
            {
                _preflightChecks.Add(new PreflightCheckViewModel(check));
            }

            var passed = report.Checks.Count(check => check.Passed);
            PreflightStatusText.Text = report.CanProceed
                ? $"READY — all {passed} preflight checks passed at {report.CompletedAt.ToLocalTime():T}."
                : $"BLOCKED — {report.Checks.Count - passed} check(s) need attention.";
            PreflightStatusText.Foreground = ResourceBrush(report.CanProceed ? "Brush.Success" : "Brush.Danger");
            AppendLog($"Preflight completed: {passed}/{report.Checks.Count} checks passed.");

            if (showResultDialog)
            {
                ShowMessage(
                    report.CanProceed
                        ? "Every required preflight check passed. Complete the backup confirmations and exact ProductType confirmation to enable the downgrade."
                        : string.Join(Environment.NewLine, report.Checks.Where(check => !check.Passed).Select(check => $"• {check.Title}: {check.Detail}")),
                    report.CanProceed ? "Preflight passed" : "Preflight blocked",
                    report.CanProceed ? MessageBoxImage.Information : MessageBoxImage.Warning);
            }
            return report.CanProceed;
        }
        catch (OperationCanceledException)
        {
            PreflightStatusText.Text = "Preflight cancelled.";
            PreflightStatusText.Foreground = ResourceBrush("Brush.TextTertiary");
            return false;
        }
        catch (Exception exception)
        {
            _lastPreflight = null;
            PreflightStatusText.Text = $"Preflight failed: {exception.Message}";
            PreflightStatusText.Foreground = ResourceBrush("Brush.Danger");
            AppendLog($"Preflight failed: {exception}");
            return false;
        }
        finally
        {
            _preflightCts?.Dispose();
            _preflightCts = null;
            RunPreflightButton.IsEnabled = !_busy;
            UpdateActionState();
        }
    }

    private void EnhancedConfirmationChanged(object sender, RoutedEventArgs e) => UpdateActionState();

    private void ProductTypeConfirmationChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        UpdateActionState();

    private bool IsEnhancedSafetyReady() => true;

    private bool IsBackupChecklistReady() =>
        BackupEncryptedCheck.IsChecked == true &&
        BackupPhotosCheck.IsChecked == true &&
        BackupAuthCheck.IsChecked == true &&
        ActivationLockCheck.IsChecked == true &&
        BackupCompatibilityCheck.IsChecked == true;

    private async Task<bool> PrepareRestoreTargetAsync(bool showDialog)
    {
        if (!File.Exists(IpswPathBox.Text))
        {
            if (showDialog)
            {
                ShowMessage(
                    "Select an iOS/iPadOS 15 IPSW first.",
                    "Restore target not ready",
                    MessageBoxImage.Information);
            }
            return false;
        }

        AppleDeviceSnapshot snapshot;
        try
        {
            snapshot = await _monitor.ProbeAsync();
            UpdateExperienceDeviceState(snapshot);
        }
        catch (Exception exception)
        {
            AppendLog($"Live device probe failed: {exception}");
            if (showDialog)
            {
                ShowMessage("Windows could not query the connected Apple USB device.", "Device detection failed", MessageBoxImage.Warning);
            }
            return false;
        }

        if (snapshot.Mode == AppleDeviceMode.Disconnected)
        {
            if (showDialog)
            {
                ShowMessage("Connect the Apple device in normal, recovery, or DFU mode, then click Downgrade again.", "Device not detected", MessageBoxImage.Information);
            }
            return false;
        }

        var liveProductType = await ResolveConnectedProductTypeAsync(snapshot, CancellationToken.None);
        if (!string.IsNullOrWhiteSpace(liveProductType))
        {
            var liveDevice = DarkSwordDeviceCatalog.Find(liveProductType);
            SetDetectedDevice(liveProductType, liveDevice, null, clearFirmware: false);
        }

        if (_inspection is null || !string.Equals(_inspection.Path, IpswPathBox.Text, StringComparison.OrdinalIgnoreCase))
        {
            CurrentDetailText.Text = "Inspecting the selected IPSW...";
            _inspection = await _inspector.InspectAsync(IpswPathBox.Text);
            IpswSummaryText.Text = _inspection.IsValid
                ? $"IPSW inspected: iOS/iPadOS {_inspection.ProductVersion} ({_inspection.BuildVersion}) for {string.Join(", ", _inspection.SupportedProductTypes)}."
                : string.Join(Environment.NewLine, _inspection.Errors);
        }

        if (_inspection?.IsValid != true)
        {
            if (showDialog)
            {
                ShowMessage(
                    _inspection is null ? "The selected IPSW could not be inspected." : string.Join(Environment.NewLine, _inspection.Errors),
                    "IPSW not ready",
                    MessageBoxImage.Warning);
            }
            return false;
        }

        var productType = DetectedProductType;
        if (string.IsNullOrWhiteSpace(productType))
        {
            if (showDialog)
            {
                ShowMessage(
                    $"The connected {snapshot.Mode} device exposes CPID/BDID/ECID data, but it does not map to a supported restore target. USB identity: {snapshot.InstanceId ?? "unavailable"}.",
                    "Restore target not ready",
                    MessageBoxImage.Information);
            }
            return false;
        }

        if (_firmwareIdentifier is null)
        {
            var device = DarkSwordDeviceCatalog.Find(productType);
            SetDetectedDevice(productType, device, $"Exact ProductType resolved from the connected {snapshot.Mode} device.", clearFirmware: false);
        }

        return IsActiveRestoreTargetReady();
    }

    private bool IsPreflightCurrent()
    {
        if (_lastPreflight?.CanProceed != true || _inspection is null) return false;
        var current = DowngradePreflightService.BuildFingerprint(
            DetectedProductType,
            IpswPathBox.Text,
            _experienceSnapshot,
            _inspection);
        return string.Equals(current, _lastPreflight.Fingerprint, StringComparison.Ordinal);
    }

    private void InvalidatePreflight(string reason)
    {
        _lastPreflight = null;
        if (_experienceInitialized)
        {
            PreflightStatusText.Text = reason;
            PreflightStatusText.Foreground = ResourceBrush("Brush.TextTertiary");
        }
    }

    private bool ConfirmFinalDowngradeSummary(RestoreSession? recoverySession = null)
    {
        if (_inspection is null || string.IsNullOrWhiteSpace(DetectedProductType)) return false;
        var device = _detectedDarkSwordDevice;
        var message = string.Join(Environment.NewLine, new[]
        {
            recoverySession is null ? "FINAL ERASE CONFIRMATION" : "FINAL RECOVERY CONFIRMATION",
            string.Empty,
            $"Device: {device?.DisplayName ?? "Apple device"}",
            $"ProductType: {DetectedProductType}",
            $"Target: iOS/iPadOS {_inspection.ProductVersion} ({_inspection.BuildVersion})",
            $"IPSW SHA-256: {_inspection.Sha256}",
            recoverySession is null
                ? "Action: Completely erase the device and perform a tethered downgrade."
                : $"Action: Resume safe checkpoint {recoverySession.SessionId}.",
            "Cold boots will continue to require this Windows application.",
            string.Empty,
            "Choose Yes only if every line above is correct."
        });
        var owner = Window.GetWindow(this);
        return MessageBox.Show(
                   owner,
                   message,
                   "Confirm exact downgrade target",
                   MessageBoxButton.YesNo,
                   MessageBoxImage.Warning,
                   MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    private void HandleEnhancedProgress(RestoreProgress progress)
    {
        UpdateProgress(progress);
        UpdateStageTimeline(progress);
        if (_recoveryService is not null)
        {
            _ = _recoveryService.MarkLatestCheckpointAsync(progress);
        }
    }

    private void BuildStageTimeline()
    {
        _stageTimeline.Clear();
        foreach (var title in new[]
                 {
                     "1. Preparing device",
                     "2. Installing DFU driver",
                     "3. Running checkm8 / PongoOS",
                     "4. Capturing SHC checkpoints",
                     "5. Restoring firmware",
                     "6. Building tether-boot profile",
                     "7. Final tether boot",
                     "8. Verifying completion"
                 })
        {
            _stageTimeline.Add(new StageTimelineItem(title));
        }
    }

    private void UpdateStageTimeline(RestoreProgress progress)
    {
        var index = progress.Stage switch
        {
            RestoreStage.Preflight or RestoreStage.WaitingForDfu => 0,
            RestoreStage.InstallingDfuDriver => 1,
            RestoreStage.EnteringPwnedDfu or RestoreStage.BootingPongo => 2,
            RestoreStage.GeneratingShcBlock => 3,
            RestoreStage.RestoringFirmware => 4,
            RestoreStage.GeneratingPteBlock => 5,
            RestoreStage.LoadingSepExploit or RestoreStage.LoadingKernelPatchfinder or RestoreStage.BootingXnu => 6,
            RestoreStage.Completed => 7,
            _ => Math.Clamp(_stageTimeline.ToList().FindIndex(item => item.State == "Active"), 0, 7)
        };

        for (var i = 0; i < _stageTimeline.Count; i++)
        {
            var item = _stageTimeline[i];
            if (progress.Stage == RestoreStage.Failed && i == index)
            {
                item.Set("Failed", "✕", progress.Detail);
            }
            else if (progress.Stage == RestoreStage.Cancelled && i == index)
            {
                item.Set("Paused", "Ⅱ", progress.Detail);
            }
            else if (i < index || progress.Stage == RestoreStage.Completed)
            {
                item.Set("Complete", "✓", i == index ? progress.Detail : "Completed");
            }
            else if (i == index)
            {
                item.Set("Active", "●", progress.Detail);
            }
            else
            {
                item.Set("Pending", "○", "Waiting");
            }
        }
    }

    private async Task RefreshRecoveryStateAsync()
    {
        if (_recoveryService is null || !_experienceInitialized) return;
        try
        {
            _recoveryCandidate = await _recoveryService.FindLatestRecoverableAsync();
            RecoveryStatusText.Text = _recoveryCandidate is null
                ? "No incomplete session with a safe retry checkpoint was found."
                : $"Session {_recoveryCandidate.Session.SessionId}: {_recoveryCandidate.Description}";
            ResumeSessionButton.IsEnabled = !_busy && _recoveryCandidate?.CanResume == true;
            RetryStageButton.IsEnabled = ResumeSessionButton.IsEnabled;
        }
        catch (Exception exception)
        {
            RecoveryStatusText.Text = $"Could not inspect recovery sessions: {exception.Message}";
            AppendLog($"Recovery scan failed: {exception}");
        }
    }

    private async void RefreshRecovery_Click(object sender, RoutedEventArgs e) =>
        await RefreshRecoveryStateAsync();

    private async void ResumeSession_Click(object sender, RoutedEventArgs e) =>
        await ResumeLatestSessionAsync();

    private async void RetryStage_Click(object sender, RoutedEventArgs e) =>
        await ResumeLatestSessionAsync();

    private async Task ResumeLatestSessionAsync()
    {
        if (_recoveryService is null || _recoveryCandidate is null || string.IsNullOrWhiteSpace(DetectedProductType)) return;
        IpswPathBox.Text = _recoveryCandidate.Session.IpswPath;
        _inspection = _recoveryCandidate.Session.Ipsw;
        InvalidatePreflight("Recovery session loaded.");
        if (!await PrepareRestoreTargetAsync(showDialog: true)) return;
        if (!IsEnhancedSafetyReady())
        {
            ShowMessage(
                "Complete all backup acknowledgements and type the exact ProductType before resuming.",
                "Recovery safety confirmation required",
                MessageBoxImage.Information);
            return;
        }
        if (!ConfirmFinalDowngradeSummary(_recoveryCandidate.Session)) return;

        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Resuming downgrade", _recoveryCandidate.Description);
        try
        {
            var session = await _recoveryService.ResumeAsync(
                _recoveryCandidate,
                DetectedProductType,
                new Progress<RestoreProgress>(HandleEnhancedProgress),
                AppendLog,
                _operationCts.Token);
            PtePathBox.Text = session.PteBlockPath ?? string.Empty;
            ShowPostDowngradeDashboard(session);
            ShowMessage("The saved downgrade checkpoint completed successfully.", "Recovery complete", MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            CurrentStageText.Text = "Paused";
            CurrentDetailText.Text = "Recovery was paused. The safe checkpoint remains available.";
            AppendLog("Recovery operation paused by the user.");
        }
        catch (Exception exception)
        {
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Recovery stopped", MessageBoxImage.Error);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            await RefreshRecoveryStateAsync();
        }
    }

    private void ShowPostDowngradeDashboard(RestoreSession session)
    {
        _completedSession = session;
        PostDowngradePanel.Visibility = Visibility.Visible;
        PostDowngradeSummaryText.Text =
            $"Expected firmware: {session.Ipsw.ProductVersion} ({session.Ipsw.BuildVersion})\n" +
            $"ProductType: {DetectedProductType ?? string.Join(", ", session.Ipsw.SupportedProductTypes)}\n" +
            $"Session: {session.SessionId}\n" +
            $"Boot asset: {session.PteBlockPath ?? "not generated"}";
        PostVerificationText.Text = "Waiting for the restored device to enumerate so ProductType and firmware can be verified.";
        PtePathBox.Text = session.PteBlockPath ?? string.Empty;
        _ = VerifyPostDowngradeAsync();
    }

    private async void VerifyPostDowngrade_Click(object sender, RoutedEventArgs e) =>
        await VerifyPostDowngradeAsync();

    private async Task VerifyPostDowngradeAsync()
    {
        if (_completedSession is null) return;
        _postVerifyCts?.Cancel();
        _postVerifyCts?.Dispose();
        _postVerifyCts = new CancellationTokenSource();
        VerifyPostDowngradeButton.IsEnabled = false;
        PostVerificationText.Text = "Waiting for normal mode and reading ProductType and ProductVersion...";

        try
        {
            var snapshot = _monitor.Current.Mode == AppleDeviceMode.Normal
                ? _monitor.Current
                : await _monitor.WaitForModeAsync(
                    new[] { AppleDeviceMode.Normal },
                    TimeSpan.FromMinutes(2),
                    _postVerifyCts.Token);
            var ideviceInfo = ResolveToolchainExecutable("ideviceinfo.exe");
            var productType = NormalizeProductType(await RunIdentityToolAsync(
                ideviceInfo,
                new[] { "-k", "ProductType" },
                _postVerifyCts.Token));
            var versionOutput = await RunIdentityToolAsync(
                ideviceInfo,
                new[] { "-k", "ProductVersion" },
                _postVerifyCts.Token);
            var version = versionOutput.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.Trim();
            var productMatches = string.Equals(productType, DetectedProductType, StringComparison.Ordinal);
            var versionMatches = string.Equals(version, _completedSession.Ipsw.ProductVersion, StringComparison.Ordinal);
            PostVerificationText.Text = productMatches && versionMatches
                ? $"VERIFIED — {productType} is running {version}. Device mode: {snapshot.Mode}."
                : $"Verification mismatch — detected {productType ?? "unknown"} on {version ?? "unknown"}; expected {DetectedProductType} on {_completedSession.Ipsw.ProductVersion}.";
            PostVerificationText.Foreground = ResourceBrush(productMatches && versionMatches ? "Brush.Success" : "Brush.Danger");
        }
        catch (TimeoutException)
        {
            PostVerificationText.Text = "The device has not reached normal mode yet. Use Boot Device, then press Verify again.";
            PostVerificationText.Foreground = ResourceBrush("Brush.Accent");
        }
        catch (OperationCanceledException)
        {
            PostVerificationText.Text = "Post-downgrade verification cancelled.";
        }
        catch (Exception exception)
        {
            PostVerificationText.Text = $"Verification could not complete: {exception.Message}";
            AppendLog($"Post-downgrade verification failed: {exception}");
        }
        finally
        {
            _postVerifyCts?.Dispose();
            _postVerifyCts = null;
            VerifyPostDowngradeButton.IsEnabled = true;
        }
    }

    private void PostBootDevice_Click(object sender, RoutedEventArgs e)
    {
        if (_completedSession?.PteBlockPath is not { Length: > 0 } pte || !File.Exists(pte)) return;
        PtePathBox.Text = pte;
        TetherBoot_Click(sender, e);
    }

    private void OpenCompletedSession_Click(object sender, RoutedEventArgs e)
    {
        if (_completedSession is not null) OpenFolder(_completedSession.SessionDirectory);
    }

    private void OpenBackupFolder_Click(object sender, RoutedEventArgs e)
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Apple Computer", "MobileSync", "Backup"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Apple", "MobileSync", "Backup")
        };
        var path = candidates.FirstOrDefault(Directory.Exists);
        if (path is null)
        {
            ShowMessage(
                "No Apple Devices/iTunes local backup folder was found. Create an encrypted local backup before continuing.",
                "Backup folder not found",
                MessageBoxImage.Information);
            return;
        }
        OpenFolder(path);
    }

    private void CopyBackupChecklist_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(
            "DarkSword pre-downgrade checklist:\n" +
            "[ ] Encrypted Apple Devices/iTunes backup completed\n" +
            "[ ] Photos and imported files copied separately\n" +
            "[ ] Authenticator recovery codes saved\n" +
            "[ ] Apple ID password and Activation Lock account known\n" +
            "[ ] I understand a backup made on newer iOS may not restore to iOS 15");
        BackupChecklistStatusText.Text = "Backup checklist copied to the clipboard.";
    }

    private void DisposeDowngradeExperience()
    {
        if (!_experienceInitialized) return;
        _firmwareItems.CollectionChanged -= FirmwareItems_CollectionChanged;
        _preflightCts?.Cancel();
        _preflightCts?.Dispose();
        _preflightCts = null;
        _postVerifyCts?.Cancel();
        _postVerifyCts?.Dispose();
        _postVerifyCts = null;
        _experienceInitialized = false;
    }

    private sealed class PreflightCheckViewModel
    {
        public PreflightCheckViewModel(PreflightCheckResult result)
        {
            Title = result.Title;
            Detail = result.Detail;
            Passed = result.Passed;
            StateText = result.Passed ? (result.WasRepaired ? "REPAIRED" : "PASS") : "BLOCKED";
            Glyph = result.Passed ? "✓" : "✕";
        }

        public string Title { get; }
        public string Detail { get; }
        public bool Passed { get; }
        public string StateText { get; }
        public string Glyph { get; }
    }

    private sealed class StageTimelineItem : INotifyPropertyChanged
    {
        private string _state = "Pending";
        private string _glyph = "○";
        private string _detail = "Waiting";

        public StageTimelineItem(string title) => Title = title;
        public string Title { get; }
        public string State { get => _state; private set { _state = value; OnPropertyChanged(); } }
        public string Glyph { get => _glyph; private set { _glyph = value; OnPropertyChanged(); } }
        public string Detail { get => _detail; private set { _detail = value; OnPropertyChanged(); } }

        public void Set(string state, string glyph, string detail)
        {
            State = state;
            Glyph = glyph;
            Detail = detail;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
