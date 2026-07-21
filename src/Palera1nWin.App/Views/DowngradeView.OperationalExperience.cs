using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DarkSwordRestore.Core;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private readonly CompatibilityAssessmentService _compatibilityService = new();
    private readonly CableStabilityTracker _cableTracker = new();
    private readonly RestoreHealthTracker _healthTracker = new();
    private readonly DeviceProfileStore _profileStore = new();
    private readonly SessionExportService _exportService = new();
    private readonly ExperiencePreferencesStore _preferencesStore = new();

    private bool _operationalExperienceInitialized;
    private bool _operationalRunActive;
    private bool _windowCloseGuardWired;
    private DispatcherTimer? _operationalTimer;
    private PowerProtectionLease? _powerProtection;
    private DeviceDowngradeProfile? _activeDeviceProfile;
    private string? _lastProfileLookupKey;
    private string? _lastExportedSessionId;
    private DowngradeUiMode _uiMode = DowngradeUiMode.Beginner;

    private ComboBox _modeSelector = null!;
    private TextBlock _modeSummaryText = null!;
    private TextBlock _nextActionText = null!;
    private Button _nextActionButton = null!;
    private TextBlock _compatibilityText = null!;
    private TextBlock _compatibilityDetailText = null!;
    private TextBlock _storageText = null!;
    private TextBlock _storageDetailText = null!;
    private TextBlock _cableText = null!;
    private TextBlock _cableDetailText = null!;
    private TextBlock _healthText = null!;
    private TextBlock _healthDetailText = null!;
    private TextBlock _profileText = null!;
    private TextBlock _profileDetailText = null!;
    private TextBlock _failureText = null!;
    private TextBlock _powerText = null!;
    private StackPanel _expertPanel = null!;
    private Button _exportSessionButton = null!;

    private void InitializeOperationalExperience()
    {
        if (_operationalExperienceInitialized) return;
        _operationalExperienceInitialized = true;

        BuildOperationalExperiencePanel();
        _monitor.DeviceChanged += Operational_DeviceChanged;
        IpswPathBox.TextChanged += Operational_StateChanged;
        FirmwareList.SelectionChanged += Operational_SelectionChanged;
        PreflightStatusText.TextChanged += Operational_StateChanged;
        CurrentStageText.TextChanged += Operational_StateChanged;
        CurrentDetailText.TextChanged += Operational_StateChanged;
        OperationProgress.ValueChanged += Operational_ProgressChanged;
        LogBox.TextChanged += Operational_LogChanged;
        PostDowngradePanel.IsVisibleChanged += Operational_PostPanelVisibilityChanged;
        Unloaded += OperationalExperience_Unloaded;

        _operationalTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _operationalTimer.Tick += OperationalTimer_Tick;
        _operationalTimer.Start();

        _ = LoadOperationalPreferencesAsync();
        _ = LoadKnownDeviceProfileAsync(_monitor.Current);
        RefreshOperationalDashboard();
    }

    private void BuildOperationalExperiencePanel()
    {
        if (Content is not ScrollViewer scroller || scroller.Content is not StackPanel root) return;

        var header = new TextBlock
        {
            Text = "OPERATIONAL SAFETY & HEALTH",
            Margin = new Thickness(0, 22, 0, 10)
        };
        if (TryFindResource("Text.Section") is Style sectionStyle) header.Style = sectionStyle;

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Background = ResourceBrush("Brush.Card"),
            BorderBrush = ResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1)
        };
        var content = new StackPanel();
        card.Child = content;

        var modeGrid = new Grid();
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var modeTitle = new TextBlock
        {
            Text = "Guidance mode",
            FontWeight = FontWeights.SemiBold,
            FontSize = 14
        };
        _modeSummaryText = new TextBlock
        {
            Text = "Beginner mode shows only the next required action.",
            Margin = new Thickness(0, 4, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            Foreground = ResourceBrush("Brush.TextTertiary")
        };
        var modeCopy = new StackPanel();
        modeCopy.Children.Add(modeTitle);
        modeCopy.Children.Add(_modeSummaryText);
        Grid.SetColumn(modeCopy, 0);
        modeGrid.Children.Add(modeCopy);

        _modeSelector = new ComboBox
        {
            Width = 150,
            MinHeight = 34,
            VerticalAlignment = VerticalAlignment.Center,
            ItemsSource = Enum.GetValues<DowngradeUiMode>()
        };
        _modeSelector.SelectionChanged += ModeSelector_SelectionChanged;
        Grid.SetColumn(_modeSelector, 1);
        modeGrid.Children.Add(_modeSelector);
        content.Children.Add(modeGrid);

        var nextActionBorder = new Border
        {
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(14),
            CornerRadius = new CornerRadius(7),
            Background = ResourceBrush("Brush.SurfaceSecondary"),
            BorderBrush = ResourceBrush("Brush.Accent"),
            BorderThickness = new Thickness(1)
        };
        var nextGrid = new Grid();
        nextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nextGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var nextCopy = new StackPanel();
        nextCopy.Children.Add(new TextBlock
        {
            Text = "NEXT ACTION",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = ResourceBrush("Brush.Accent")
        });
        _nextActionText = new TextBlock
        {
            Margin = new Thickness(0, 5, 12, 0),
            TextWrapping = TextWrapping.Wrap,
            FontSize = 13
        };
        nextCopy.Children.Add(_nextActionText);
        Grid.SetColumn(nextCopy, 0);
        nextGrid.Children.Add(nextCopy);
        _nextActionButton = new Button
        {
            Content = "Continue",
            MinWidth = 130,
            MinHeight = 38,
            Padding = new Thickness(16, 7, 16, 7),
            VerticalAlignment = VerticalAlignment.Center
        };
        _nextActionButton.Click += NextActionButton_Click;
        Grid.SetColumn(_nextActionButton, 1);
        nextGrid.Children.Add(_nextActionButton);
        nextActionBorder.Child = nextGrid;
        content.Children.Add(nextActionBorder);

        var statusGrid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        statusGrid.Children.Add(CreateOperationalStatusCard("Compatibility score", out _compatibilityText, out _compatibilityDetailText, 0, 0));
        statusGrid.Children.Add(CreateOperationalStatusCard("Storage planner", out _storageText, out _storageDetailText, 0, 1));
        statusGrid.Children.Add(CreateOperationalStatusCard("Cable stability", out _cableText, out _cableDetailText, 1, 0));
        statusGrid.Children.Add(CreateOperationalStatusCard("Restore health", out _healthText, out _healthDetailText, 1, 1));
        statusGrid.Children.Add(CreateOperationalStatusCard("Known-device profile", out _profileText, out _profileDetailText, 2, 0));

        var powerCard = CreateOperationalStatusCard("Power-loss protection", out _powerText, out var powerDetail, 2, 1);
        powerDetail.Text = "Windows sleep, display sleep, and accidental app closure are blocked while a downgrade operation is active.";
        statusGrid.Children.Add(powerCard);
        content.Children.Add(statusGrid);

        _failureText = new TextBlock
        {
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(13),
            Text = "Failure-specific guidance will appear here when the app recognizes a driver, DFU, USB, firmware, restore, or SEP error.",
            TextWrapping = TextWrapping.Wrap,
            Background = ResourceBrush("Brush.SurfaceSecondary"),
            Foreground = ResourceBrush("Brush.TextTertiary")
        };
        content.Children.Add(_failureText);

        _expertPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        _expertPanel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 14) });
        _expertPanel.Children.Add(new TextBlock
        {
            Text = "EXPERT CONTROLS",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = ResourceBrush("Brush.Accent")
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _exportSessionButton = CreateOperationalButton("Export Latest Session", ExportLatestSession_Click);
        buttons.Children.Add(_exportSessionButton);
        buttons.Children.Add(CreateOperationalButton("Open Exports", OpenExports_Click));
        buttons.Children.Add(CreateOperationalButton("Device Profiles", OpenProfiles_Click));
        buttons.Children.Add(CreateOperationalButton("Recalculate", RecalculateOperational_Click));
        _expertPanel.Children.Add(buttons);
        content.Children.Add(_expertPanel);

        var insertIndex = Math.Max(0, root.Children.Count - 4);
        root.Children.Insert(insertIndex, header);
        root.Children.Insert(insertIndex + 1, card);
    }

    private Border CreateOperationalStatusCard(
        string title,
        out TextBlock status,
        out TextBlock detail,
        int row,
        int column)
    {
        var border = new Border
        {
            Margin = new Thickness(column == 0 ? 0 : 6, row == 0 ? 0 : 6, column == 0 ? 6 : 0, 0),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = ResourceBrush("Brush.SurfaceSecondary"),
            BorderBrush = ResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 12
        });
        status = new TextBlock
        {
            Text = "Checking...",
            Margin = new Thickness(0, 5, 0, 0),
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        detail = new TextBlock
        {
            Margin = new Thickness(0, 4, 0, 0),
            FontSize = 11.5,
            Foreground = ResourceBrush("Brush.TextTertiary"),
            TextWrapping = TextWrapping.Wrap
        };
        stack.Children.Add(status);
        stack.Children.Add(detail);
        border.Child = stack;
        Grid.SetRow(border, row);
        Grid.SetColumn(border, column);
        return border;
    }

    private static Button CreateOperationalButton(string text, RoutedEventHandler handler)
    {
        var button = new Button
        {
            Content = text,
            MinHeight = 34,
            Padding = new Thickness(14, 6, 14, 6),
            Margin = new Thickness(0, 0, 8, 8)
        };
        button.Click += handler;
        return button;
    }

    private async Task LoadOperationalPreferencesAsync()
    {
        _uiMode = await _preferencesStore.LoadAsync();
        if (!_operationalExperienceInitialized) return;
        _modeSelector.SelectedItem = _uiMode;
        ApplyOperationalMode();
    }

    private async void ModeSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modeSelector.SelectedItem is not DowngradeUiMode mode) return;
        _uiMode = mode;
        ApplyOperationalMode();
        await _preferencesStore.SaveAsync(mode);
    }

    private void ApplyOperationalMode()
    {
        if (!_operationalExperienceInitialized) return;
        var expert = _uiMode == DowngradeUiMode.Expert;
        _expertPanel.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsBox.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        LogBox.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        RetryStageButton.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        _modeSummaryText.Text = expert
            ? "Expert mode exposes native logs, targeted retries, session exports, device profiles, and detailed health information."
            : "Beginner mode hides technical noise and keeps one clear next action visible.";
        RefreshOperationalDashboard();
    }

    private void Operational_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot)
    {
        _cableTracker.Observe(snapshot);
        _healthTracker.ObserveDevice(snapshot);
        Dispatcher.BeginInvoke(() =>
        {
            RefreshOperationalDashboard();
            _ = LoadKnownDeviceProfileAsync(snapshot);
        });
    }

    private void Operational_StateChanged(object sender, TextChangedEventArgs e) => RefreshOperationalDashboard();

    private void Operational_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshOperationalDashboard();

    private void Operational_ProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _healthTracker.ObserveProgress(InferOperationalStage(), OperationProgress.Value);
        RefreshOperationalDashboard();
    }

    private void Operational_LogChanged(object sender, TextChangedEventArgs e)
    {
        _healthTracker.PulseLog();
        var text = LogBox.Text;
        if (text.Length > 2400) text = text[^2400..];
        if (ContainsFailureSignal(text))
        {
            if (text.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("I/O", StringComparison.OrdinalIgnoreCase))
            {
                _cableTracker.RecordTransferError();
            }
            ShowFailureGuidance(text, InferOperationalStage());
        }
        RefreshOperationalDashboard();
    }

    private async void Operational_PostPanelVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PostDowngradePanel.Visibility != Visibility.Visible || _completedSession is null) return;
        await SaveProfileAndExportSessionAsync(_completedSession, automatic: true);
    }

    private void OperationalTimer_Tick(object? sender, EventArgs e)
    {
        if (_busy && !_operationalRunActive)
        {
            BeginOperationalProtection();
        }
        else if (!_busy && _operationalRunActive)
        {
            EndOperationalProtection();
        }
        RefreshOperationalDashboard();
    }

    private void BeginOperationalProtection()
    {
        _operationalRunActive = true;
        _powerProtection?.Dispose();
        _powerProtection = new PowerProtectionLease();
        _healthTracker.Start(_monitor.Current);
        WireWindowCloseGuard();
        AppendLog("Operational protection enabled: Windows sleep/display sleep and accidental app closure are blocked.");
    }

    private void EndOperationalProtection()
    {
        _operationalRunActive = false;
        _healthTracker.Stop();
        _powerProtection?.Dispose();
        _powerProtection = null;
        AppendLog("Operational protection released.");
    }

    private void WireWindowCloseGuard()
    {
        if (_windowCloseGuardWired || Window.GetWindow(this) is not { } owner) return;
        _windowCloseGuardWired = true;
        owner.Closing += OperationalWindow_Closing;
    }

    private void OperationalWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy || _operationCts is null) return;
        e.Cancel = true;
        ShowMessage(
            "Palera1nWin cannot close while a downgrade stage is active. Use Cancel/Pause on the Downgrade page so the session checkpoint can be saved safely.",
            "Downgrade protection active",
            MessageBoxImage.Warning);
    }

    private void RefreshOperationalDashboard()
    {
        if (!_operationalExperienceInitialized) return;

        var resources = Path.Combine(_tools.Root, "resources");
        var toolchainReady = _tools.MissingFiles().Count == 0 &&
                             File.Exists(Path.Combine(resources, "sep_racer.bin")) &&
                             File.Exists(Path.Combine(resources, "kpf.bin"));
        var assessment = _compatibilityService.Assess(
            _detectedDarkSwordDevice,
            _inspection,
            _lastPreflight,
            toolchainReady,
            IsAdministrator());
        _compatibilityText.Text = assessment.Summary;
        _compatibilityText.Foreground = ResourceBrush(
            assessment.Rating is "READY" or "GOOD" ? "Brush.Success" :
            assessment.Rating == "UNSUPPORTED" ? "Brush.Danger" : "Brush.Accent");
        _compatibilityDetailText.Text = string.Join(" ", assessment.Reasons.Take(_uiMode == DowngradeUiMode.Expert ? 8 : 2));

        try
        {
            var plan = DowngradeStoragePlanner.Calculate(IpswPathBox.Text, AppContext.BaseDirectory);
            _storageText.Text = plan.HasEnoughSpace ? $"PASS — {plan.Summary}" : $"BLOCKED — {plan.Summary}";
            _storageText.Foreground = ResourceBrush(plan.HasEnoughSpace ? "Brush.Success" : "Brush.Danger");
            _storageDetailText.Text = _uiMode == DowngradeUiMode.Expert
                ? plan.Details.Replace(Environment.NewLine, " • ")
                : "Includes IPSW, extraction, restore cache, session assets, logs, and a safety margin.";
        }
        catch (Exception exception)
        {
            _storageText.Text = "Storage could not be calculated";
            _storageDetailText.Text = exception.Message;
            _storageText.Foreground = ResourceBrush("Brush.Danger");
        }

        var cable = _cableTracker.GetSnapshot();
        _cableText.Text = cable.Summary;
        _cableText.Foreground = ResourceBrush(cable.IsHealthy ? "Brush.Success" : "Brush.Danger");
        _cableDetailText.Text = cable.Recommendation;

        var health = _healthTracker.GetSnapshot();
        _healthText.Text = health.State;
        _healthText.Foreground = ResourceBrush(health.State switch
        {
            "HEALTHY" => "Brush.Success",
            "WARNING" => "Brush.Accent",
            "CRITICAL" => "Brush.Danger",
            _ => "Brush.TextTertiary"
        });
        _healthDetailText.Text = health.ActiveTools.Count == 0
            ? health.Summary
            : $"{health.Summary} Active native tools: {string.Join(", ", health.ActiveTools)}.";

        _profileText.Text = _activeDeviceProfile is null
            ? "No saved configuration loaded"
            : $"Loaded {_activeDeviceProfile.ProductType} profile";
        _profileDetailText.Text = _activeDeviceProfile is null
            ? "A profile is saved automatically after a successful downgrade."
            : $"Last target {_activeDeviceProfile.LastVersion ?? "unknown"}; updated {_activeDeviceProfile.UpdatedAt.ToLocalTime():g}.";

        _powerText.Text = _operationalRunActive
            ? "ACTIVE — sleep and close protection enabled"
            : "Standby — activates automatically when work starts";
        _powerText.Foreground = ResourceBrush(_operationalRunActive ? "Brush.Success" : "Brush.TextTertiary");

        var advice = ModeRecoveryAdvisor.GetAdvice(_monitor.Current, InferOperationalStage());
        _nextActionText.Text = $"{advice.Title}: {advice.Action}";
        ConfigureNextActionButton(advice);
        _exportSessionButton.IsEnabled = _completedSession is not null || _recoveryCandidate?.Session is not null;
    }

    private void ConfigureNextActionButton(ModeRecoveryAdvice advice)
    {
        if (_busy)
        {
            _nextActionButton.Content = "Operation Active";
            _nextActionButton.IsEnabled = false;
            return;
        }
        _nextActionButton.IsEnabled = true;
        _nextActionButton.Content = _monitor.Current.Mode switch
        {
            AppleDeviceMode.Disconnected => "Refresh Device",
            AppleDeviceMode.Recovery => "Start DFU Guide",
            AppleDeviceMode.Dfu => _lastPreflight?.CanProceed == true ? "Review & Start" : "Run Preflight",
            AppleDeviceMode.Normal => File.Exists(IpswPathBox.Text) ? "Run Preflight" : "Refresh Firmware",
            _ when _recoveryCandidate?.CanResume == true => "Resume Safe Stage",
            _ => "Refresh Status"
        };
    }

    private async void NextActionButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_recoveryCandidate?.CanResume == true && CurrentStageText.Text.Contains("stopped", StringComparison.OrdinalIgnoreCase))
        {
            await ResumeLatestSessionAsync();
            return;
        }
        switch (_monitor.Current.Mode)
        {
            case AppleDeviceMode.Recovery:
                StartDfuGuide_Click(StartDfuGuideButton, new RoutedEventArgs());
                break;
            case AppleDeviceMode.Dfu:
                if (_lastPreflight?.CanProceed == true)
                    StartDowngradeButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                else
                    await RunPreflightAsync(showResultDialog: true);
                break;
            case AppleDeviceMode.Normal:
                if (File.Exists(IpswPathBox.Text))
                    await RunPreflightAsync(showResultDialog: true);
                else
                    RefreshFirmware_Click(RefreshFirmwareButton, new RoutedEventArgs());
                break;
            default:
                RefreshFirmware_Click(RefreshFirmwareButton, new RoutedEventArgs());
                break;
        }
    }

    private async Task LoadKnownDeviceProfileAsync(AppleDeviceSnapshot snapshot)
    {
        if (!_operationalExperienceInitialized) return;
        await Task.Delay(500);
        var productType = DetectedProductType;
        var key = DeviceProfileStore.BuildKey(productType, snapshot.Ecid, snapshot.InstanceId);
        if (string.Equals(key, _lastProfileLookupKey, StringComparison.Ordinal)) return;
        _lastProfileLookupKey = key;
        _activeDeviceProfile = await _profileStore.FindAsync(productType, snapshot.Ecid, snapshot.InstanceId);
        if (_activeDeviceProfile is not null)
        {
            if (!File.Exists(IpswPathBox.Text) && File.Exists(_activeDeviceProfile.LastIpswPath))
            {
                IpswPathBox.Text = _activeDeviceProfile.LastIpswPath!;
                _inspection = null;
                IpswSummaryText.Text = "A previously used exact-device IPSW was loaded from this device profile. Inspect it before preflight.";
            }
            if (!File.Exists(PtePathBox.Text) && File.Exists(_activeDeviceProfile.LastPteBlockPath))
            {
                PtePathBox.Text = _activeDeviceProfile.LastPteBlockPath!;
            }
        }
        RefreshOperationalDashboard();
    }

    private async Task SaveProfileAndExportSessionAsync(RestoreSession session, bool automatic)
    {
        if (automatic && string.Equals(_lastExportedSessionId, session.SessionId, StringComparison.Ordinal)) return;
        try
        {
            var snapshot = _monitor.Current;
            var productType = DetectedProductType ?? session.Ipsw.SupportedProductTypes.FirstOrDefault() ?? "unknown";
            var device = DarkSwordDeviceCatalog.Find(productType);
            var profile = new DeviceDowngradeProfile(
                DeviceProfileStore.BuildKey(productType, snapshot.Ecid, snapshot.InstanceId),
                productType,
                device?.DisplayName ?? snapshot.DisplayName ?? "Apple device",
                snapshot.Ecid,
                snapshot.InstanceId,
                session.IpswPath,
                session.Ipsw.ProductVersion,
                session.Ipsw.BuildVersion,
                session.Ipsw.Sha256,
                session.PteBlockPath,
                session.SessionDirectory,
                DateTimeOffset.UtcNow);
            await _profileStore.SaveAsync(profile);
            _activeDeviceProfile = profile;
            var export = await _exportService.ExportAsync(
                session,
                _logPath,
                profile,
                _cableTracker.GetSnapshot());
            _lastExportedSessionId = session.SessionId;
            _profileText.Text = "Profile and portable recovery ZIP saved";
            _profileDetailText.Text = export;
            AppendLog($"Saved device profile and portable session export: {export}");
        }
        catch (Exception exception)
        {
            _profileText.Text = "Automatic session export failed";
            _profileDetailText.Text = exception.Message;
            AppendLog($"Session export failed: {exception}");
        }
    }

    private async void ExportLatestSession_Click(object sender, RoutedEventArgs e)
    {
        var session = _completedSession ?? _recoveryCandidate?.Session ?? await FindLatestSessionAsync();
        if (session is null)
        {
            ShowMessage("No downgrade session is available to export.", "Session export", MessageBoxImage.Information);
            return;
        }
        await SaveProfileAndExportSessionAsync(session, automatic: false);
        ShowMessage("The portable session ZIP and device profile were saved in the DarkSword exports folder.", "Session exported", MessageBoxImage.Information);
    }

    private async Task<RestoreSession?> FindLatestSessionAsync()
    {
        if (!Directory.Exists(_sessions.RootDirectory)) return null;
        var directory = Directory.EnumerateDirectories(_sessions.RootDirectory)
            .OrderByDescending(Directory.GetLastWriteTimeUtc)
            .FirstOrDefault();
        return directory is null ? null : await _sessions.LoadAsync(directory, CancellationToken.None);
    }

    private void OpenExports_Click(object sender, RoutedEventArgs e) => OpenFolder(_exportService.ExportDirectory);

    private void OpenProfiles_Click(object sender, RoutedEventArgs e) => OpenFolder(_profileStore.RootDirectory);

    private void RecalculateOperational_Click(object sender, RoutedEventArgs e) => RefreshOperationalDashboard();

    private void ShowFailureGuidance(string message, RestoreStage? stage)
    {
        var guidance = DowngradeFailureTranslator.Translate(message, stage);
        _failureText.Text = guidance.DisplayText;
        _failureText.Foreground = ResourceBrush("Brush.Danger");
    }

    private RestoreStage InferOperationalStage()
    {
        var text = CurrentStageText.Text ?? string.Empty;
        if (text.Contains("driver", StringComparison.OrdinalIgnoreCase)) return RestoreStage.InstallingDfuDriver;
        if (text.Contains("Pongo", StringComparison.OrdinalIgnoreCase) || text.Contains("checkm8", StringComparison.OrdinalIgnoreCase)) return RestoreStage.BootingPongo;
        if (text.Contains("SHC", StringComparison.OrdinalIgnoreCase)) return RestoreStage.GeneratingShcBlock;
        if (text.Contains("restore", StringComparison.OrdinalIgnoreCase)) return RestoreStage.RestoringFirmware;
        if (text.Contains("PTE", StringComparison.OrdinalIgnoreCase) || text.Contains("profile", StringComparison.OrdinalIgnoreCase)) return RestoreStage.GeneratingPteBlock;
        if (text.Contains("SEP", StringComparison.OrdinalIgnoreCase)) return RestoreStage.LoadingSepExploit;
        if (text.Contains("boot", StringComparison.OrdinalIgnoreCase)) return RestoreStage.BootingXnu;
        if (text.Contains("complete", StringComparison.OrdinalIgnoreCase) || OperationProgress.Value >= 100) return RestoreStage.Completed;
        if (text.Contains("cancel", StringComparison.OrdinalIgnoreCase) || text.Contains("paused", StringComparison.OrdinalIgnoreCase)) return RestoreStage.Cancelled;
        return _busy ? RestoreStage.Preflight : RestoreStage.Idle;
    }

    private static bool ContainsFailureSignal(string text) =>
        text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("exited with code", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("disconnected", StringComparison.OrdinalIgnoreCase);

    private void OperationalExperience_Unloaded(object sender, RoutedEventArgs e) => DisposeOperationalExperience();

    private void DisposeOperationalExperience()
    {
        if (!_operationalExperienceInitialized) return;
        _operationalExperienceInitialized = false;
        _monitor.DeviceChanged -= Operational_DeviceChanged;
        IpswPathBox.TextChanged -= Operational_StateChanged;
        FirmwareList.SelectionChanged -= Operational_SelectionChanged;
        PreflightStatusText.TextChanged -= Operational_StateChanged;
        CurrentStageText.TextChanged -= Operational_StateChanged;
        CurrentDetailText.TextChanged -= Operational_StateChanged;
        OperationProgress.ValueChanged -= Operational_ProgressChanged;
        LogBox.TextChanged -= Operational_LogChanged;
        PostDowngradePanel.IsVisibleChanged -= Operational_PostPanelVisibilityChanged;
        Unloaded -= OperationalExperience_Unloaded;
        if (_operationalTimer is not null)
        {
            _operationalTimer.Stop();
            _operationalTimer.Tick -= OperationalTimer_Tick;
            _operationalTimer = null;
        }
        EndOperationalProtection();
        if (_windowCloseGuardWired && Window.GetWindow(this) is { } owner)
        {
            owner.Closing -= OperationalWindow_Closing;
            _windowCloseGuardWired = false;
        }
    }
}
