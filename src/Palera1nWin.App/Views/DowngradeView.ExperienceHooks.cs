using System.IO;
using System.Windows;
using DarkSwordRestore.Core;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _experienceHooksWired;
    private bool _refreshingEnhancedActions;

    private void WireDowngradeExperienceHooks()
    {
        if (_experienceHooksWired) return;
        _experienceHooksWired = true;
        InitializeDowngradeExperience();

        StartDowngradeButton.Click -= StartDowngrade_Click;
        StartDowngradeButton.Click += StartEnhancedDowngrade_Click;
        StartDowngradeButton.IsEnabledChanged += EnhancedAction_IsEnabledChanged;

        _monitor.DeviceChanged += Experience_DeviceChanged;
        IpswPathBox.TextChanged += ExperienceFirmwareChanged;
        FirmwareList.SelectionChanged += ExperienceFirmwareSelectionChanged;
        BackupEncryptedCheck.Checked += ExperienceSafetyChanged;
        BackupEncryptedCheck.Unchecked += ExperienceSafetyChanged;
        BackupPhotosCheck.Checked += ExperienceSafetyChanged;
        BackupPhotosCheck.Unchecked += ExperienceSafetyChanged;
        BackupAuthCheck.Checked += ExperienceSafetyChanged;
        BackupAuthCheck.Unchecked += ExperienceSafetyChanged;
        ActivationLockCheck.Checked += ExperienceSafetyChanged;
        ActivationLockCheck.Unchecked += ExperienceSafetyChanged;
        BackupCompatibilityCheck.Checked += ExperienceSafetyChanged;
        BackupCompatibilityCheck.Unchecked += ExperienceSafetyChanged;
        EraseCheck.Checked += ExperienceSafetyChanged;
        EraseCheck.Unchecked += ExperienceSafetyChanged;
        TetherCheck.Checked += ExperienceSafetyChanged;
        TetherCheck.Unchecked += ExperienceSafetyChanged;
        OwnershipCheck.Checked += ExperienceSafetyChanged;
        OwnershipCheck.Unchecked += ExperienceSafetyChanged;
        ProductTypeConfirmationBox.TextChanged += ExperienceProductTypeChanged;

        Loaded += Experience_Loaded;
        RefreshEnhancedActionState();
    }

    private async void Experience_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            UpdateExperienceDeviceState(await _monitor.ProbeAsync());
        }
        catch (Exception exception)
        {
            AppendLog($"Enhanced experience device probe failed: {exception.Message}");
        }
        await RefreshRecoveryStateAsync();
        RefreshEnhancedActionState();
    }

    private void Experience_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(() =>
        {
            UpdateExperienceDeviceState(snapshot);
            RefreshEnhancedActionState();
        });

    private void ExperienceFirmwareChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        InvalidatePreflight("Firmware path changed. Inspect the IPSW and run preflight again.");
        RefreshEnhancedActionState();
    }

    private void ExperienceFirmwareSelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_lastPreflight is not null)
        {
            InvalidatePreflight("Firmware selection changed. Download or inspect it, then run preflight again.");
        }
        RefreshEnhancedActionState();
    }

    private void ExperienceSafetyChanged(object sender, RoutedEventArgs e) => RefreshEnhancedActionState();

    private void ExperienceProductTypeChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) =>
        RefreshEnhancedActionState();

    private void EnhancedAction_IsEnabledChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (!_refreshingEnhancedActions) RefreshEnhancedActionState();
    }

    private void RefreshEnhancedActionState()
    {
        if (!_experienceInitialized || _refreshingEnhancedActions) return;
        _refreshingEnhancedActions = true;
        try
        {
            var confirmations = EraseCheck.IsChecked == true &&
                                TetherCheck.IsChecked == true &&
                                OwnershipCheck.IsChecked == true;
            var resources = Path.Combine(_tools.Root, "resources");
            var toolchainReady = _tools.MissingFiles().Count == 0 &&
                                 File.Exists(Path.Combine(resources, "sep_racer.bin")) &&
                                 File.Exists(Path.Combine(resources, "kpf.bin"));
            StartDowngradeButton.IsEnabled = !_busy &&
                                             confirmations &&
                                             toolchainReady &&
                                             IsActiveRestoreTargetReady() &&
                                             IsEnhancedSafetyReady();
            RunPreflightButton.IsEnabled = !_busy && _preflightCts is null;
            ResumeSessionButton.IsEnabled = !_busy && _recoveryCandidate?.CanResume == true;
            RetryStageButton.IsEnabled = ResumeSessionButton.IsEnabled;
        }
        finally
        {
            _refreshingEnhancedActions = false;
        }
    }

    private async void StartEnhancedDowngrade_Click(object sender, RoutedEventArgs e)
    {
        if (!IsActiveRestoreTargetReady())
        {
            ShowMessage(
                "The active Windows restore path requires a detected A9/A9X device and an inspected iOS/iPadOS 15 IPSW containing that exact ProductType.",
                "Restore target not ready",
                MessageBoxImage.Information);
            return;
        }

        if (!IsPreflightCurrent() && !await RunPreflightAsync(showResultDialog: false)) return;
        if (!IsEnhancedSafetyReady())
        {
            ShowMessage(
                "Complete all five backup/account items, all three erase acknowledgements, and type the exact ProductType shown by the app.",
                "Safety confirmations required",
                MessageBoxImage.Information);
            return;
        }
        if (!ConfirmFinalDowngradeSummary()) return;

        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        SetBusy(true, "Starting downgrade", "Using the verified exact-device preflight report and creating recovery checkpoints.");
        RefreshEnhancedActionState();

        try
        {
            var session = await _orchestrator.RunFullDowngradeAsync(
                _inspection!.Path,
                destructiveOperationConfirmed: true,
                new Progress<RestoreProgress>(HandleEnhancedProgress),
                AppendLog,
                _operationCts.Token);

            PtePathBox.Text = session.PteBlockPath ?? string.Empty;
            ShowPostDowngradeDashboard(session);
            await RefreshRecoveryStateAsync();
            ShowMessage(
                $"Downgrade completed.\n\nSession: {session.SessionId}\nBoot asset: {session.PteBlockPath}\n\nThe post-downgrade dashboard is now verifying the device.",
                "Downgrade complete",
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            var progress = new RestoreProgress(RestoreStage.Cancelled, OperationProgress.Value, "Paused", "The operation was paused. Completed safe checkpoints remain in the session folder.");
            HandleEnhancedProgress(progress);
            AppendLog("Downgrade operation paused by the user.");
        }
        catch (Exception exception)
        {
            var stage = exception is DarkSwordException darkSword ? darkSword.Stage : RestoreStage.Failed;
            HandleEnhancedProgress(new RestoreProgress(stage == RestoreStage.Completed ? RestoreStage.Failed : RestoreStage.Failed, OperationProgress.Value, "Downgrade stopped", exception.Message));
            AppendLog(exception.ToString());
            ShowMessage(
                exception.Message + "\n\nOpen Recovery & Targeted Retry to resume from the newest safe checkpoint.",
                "Downgrade stopped",
                MessageBoxImage.Error);
        }
        finally
        {
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            await RefreshRecoveryStateAsync();
            RefreshEnhancedActionState();
        }
    }
}
