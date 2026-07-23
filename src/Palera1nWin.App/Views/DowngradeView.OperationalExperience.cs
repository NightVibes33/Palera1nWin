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
    private bool _operationalInitializationQueued;
    private bool _operationalRunActive;
    private bool _windowCloseGuardWired;
    private DispatcherTimer? _operationalTimer;
    private PowerProtectionLease? _powerProtection;
    private DeviceDowngradeProfile? _activeDeviceProfile;
    private string? _lastProfileLookupKey;
    private string? _lastExportedSessionId;
    private DowngradeUiMode _uiMode = DowngradeUiMode.Beginner;

    private ComboBox _modeSelector = null!;
    private TextBlock _modeSummary = null!;
    private TextBlock _nextAction = null!;
    private Button _nextActionButton = null!;
    private TextBlock _compatibility = null!;
    private TextBlock _compatibilityDetail = null!;
    private TextBlock _storage = null!;
    private TextBlock _storageDetail = null!;
    private TextBlock _cable = null!;
    private TextBlock _cableDetail = null!;
    private TextBlock _health = null!;
    private TextBlock _healthDetail = null!;
    private TextBlock _profile = null!;
    private TextBlock _profileDetail = null!;
    private TextBlock _power = null!;
    private TextBlock _failure = null!;
    private StackPanel _expertPanel = null!;
    private Button _exportButton = null!;

    private bool OperationalDashboardControlsReady =>
        _modeSelector is not null &&
        _modeSummary is not null &&
        _nextAction is not null &&
        _nextActionButton is not null &&
        _compatibility is not null &&
        _compatibilityDetail is not null &&
        _storage is not null &&
        _storageDetail is not null &&
        _cable is not null &&
        _cableDetail is not null &&
        _health is not null &&
        _healthDetail is not null &&
        _profile is not null &&
        _profileDetail is not null &&
        _power is not null &&
        _failure is not null &&
        _expertPanel is not null &&
        _exportButton is not null;

    private bool OperationalDashboardReady =>
        _operationalExperienceInitialized && OperationalDashboardControlsReady;

    private void InitializeOperationalExperience()
    {
        if (_operationalExperienceInitialized || _disposed) return;
        if (!BuildOperationalPanel())
        {
            QueueOperationalInitializationRetry();
            return;
        }

        _operationalExperienceInitialized = true;
        _monitor.DeviceChanged += Operational_DeviceChanged;
        IpswPathBox.TextChanged += Operational_TextChanged;
        FirmwareList.SelectionChanged += Operational_SelectionChanged;
        OperationProgress.ValueChanged += Operational_ProgressChanged;
        LogBox.TextChanged += Operational_LogChanged;
        PostDowngradePanel.IsVisibleChanged += Operational_PostPanelChanged;
        Unloaded += Operational_Unloaded;

        _operationalTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(2)
        };
        _operationalTimer.Tick += OperationalTimer_Tick;
        _operationalTimer.Start();

        _ = LoadModeAsync();
        _ = LoadKnownProfileAsync(_monitor.Current);
        RefreshOperationalDashboard();
    }

    private void QueueOperationalInitializationRetry()
    {
        if (_operationalInitializationQueued || _disposed) return;
        _operationalInitializationQueued = true;
        Dispatcher.BeginInvoke(
            DispatcherPriority.Loaded,
            new Action(() =>
            {
                _operationalInitializationQueued = false;
                if (!_disposed && IsLoaded) InitializeOperationalExperience();
            }));
    }

    private bool BuildOperationalPanel()
    {
        if (OperationalDashboardControlsReady) return true;
        var root = FindOperationalRootPanel(Content as DependencyObject);
        if (root is null) return false;

        var header = new TextBlock { Text = "OPERATIONAL SAFETY & HEALTH", Margin = new Thickness(0, 22, 0, 10) };
        if (TryFindResource("Text.Section") is Style style) header.Style = style;

        var card = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(18),
            Background = ResourceBrush("Brush.Card"),
            BorderBrush = ResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel();
        card.Child = stack;

        var modeGrid = new Grid();
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        modeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var modeCopy = new StackPanel();
        modeCopy.Children.Add(new TextBlock { Text = "Guidance mode", FontWeight = FontWeights.SemiBold, FontSize = 14 });
        _modeSummary = Caption("Beginner mode shows one clear next action.");
        modeCopy.Children.Add(_modeSummary);
        modeGrid.Children.Add(modeCopy);
        _modeSelector = new ComboBox
        {
            Width = 150,
            MinHeight = 34,
            ItemsSource = Enum.GetValues<DowngradeUiMode>(),
            VerticalAlignment = VerticalAlignment.Center
        };
        _modeSelector.SelectionChanged += ModeSelector_Changed;
        Grid.SetColumn(_modeSelector, 1);
        modeGrid.Children.Add(_modeSelector);
        stack.Children.Add(modeGrid);

        var nextBorder = new Border
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
        _nextAction = new TextBlock { Margin = new Thickness(0, 5, 12, 0), TextWrapping = TextWrapping.Wrap };
        nextCopy.Children.Add(_nextAction);
        nextGrid.Children.Add(nextCopy);
        _nextActionButton = NewButton("Continue", NextAction_Click);
        _nextActionButton.MinWidth = 135;
        Grid.SetColumn(_nextActionButton, 1);
        nextGrid.Children.Add(_nextActionButton);
        nextBorder.Child = nextGrid;
        stack.Children.Add(nextBorder);

        var statusGrid = new Grid { Margin = new Thickness(0, 16, 0, 0) };
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        statusGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var index = 0; index < 3; index++) statusGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        statusGrid.Children.Add(StatusCard("Compatibility score", out _compatibility, out _compatibilityDetail, 0, 0));
        statusGrid.Children.Add(StatusCard("Storage planner", out _storage, out _storageDetail, 0, 1));
        statusGrid.Children.Add(StatusCard("Cable stability", out _cable, out _cableDetail, 1, 0));
        statusGrid.Children.Add(StatusCard("Restore health", out _health, out _healthDetail, 1, 1));
        statusGrid.Children.Add(StatusCard("Known-device profile", out _profile, out _profileDetail, 2, 0));
        var powerCard = StatusCard("Power-loss protection", out _power, out var powerDetail, 2, 1);
        powerDetail.Text = "Blocks Windows sleep, display sleep, and accidental app closure during active work.";
        statusGrid.Children.Add(powerCard);
        stack.Children.Add(statusGrid);

        _failure = new TextBlock
        {
            Margin = new Thickness(0, 16, 0, 0),
            Padding = new Thickness(13),
            TextWrapping = TextWrapping.Wrap,
            Text = "Failure-specific help appears here when a known DFU, USB, firmware, restore, Pongo, or SEP error is detected.",
            Background = ResourceBrush("Brush.SurfaceSecondary"),
            Foreground = ResourceBrush("Brush.TextTertiary")
        };
        stack.Children.Add(_failure);

        _expertPanel = new StackPanel { Margin = new Thickness(0, 16, 0, 0) };
        _expertPanel.Children.Add(new Separator { Margin = new Thickness(0, 0, 0, 12) });
        _expertPanel.Children.Add(new TextBlock
        {
            Text = "EXPERT CONTROLS",
            FontWeight = FontWeights.Bold,
            FontSize = 11,
            Foreground = ResourceBrush("Brush.Accent")
        });
        var buttons = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        _exportButton = NewButton("Export Latest Session", ExportLatest_Click);
        buttons.Children.Add(_exportButton);
        buttons.Children.Add(NewButton("Open Exports", OpenExports_Click));
        buttons.Children.Add(NewButton("Device Profiles", OpenProfiles_Click));
        buttons.Children.Add(NewButton("Recalculate", Recalculate_Click));
        _expertPanel.Children.Add(buttons);
        stack.Children.Add(_expertPanel);

        var insert = Math.Max(0, root.Children.Count - 4);
        root.Children.Insert(insert, header);
        root.Children.Insert(insert + 1, card);
        return OperationalDashboardControlsReady;
    }

    private static StackPanel? FindOperationalRootPanel(DependencyObject? node)
    {
        if (node is ScrollViewer { Content: StackPanel directRoot }) return directRoot;
        if (node is null) return null;

        foreach (var child in LogicalTreeHelper.GetChildren(node))
        {
            if (child is not DependencyObject dependency) continue;
            var found = FindOperationalRootPanel(dependency);
            if (found is not null) return found;
        }

        return null;
    }

    private Border StatusCard(string title, out TextBlock status, out TextBlock detail, int row, int column)
    {
        var card = new Border
        {
            Margin = new Thickness(column == 0 ? 0 : 6, row == 0 ? 0 : 6, column == 0 ? 6 : 0, 0),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(6),
            Background = ResourceBrush("Brush.SurfaceSecondary"),
            BorderBrush = ResourceBrush("Brush.Border"),
            BorderThickness = new Thickness(1)
        };
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock { Text = title, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        status = new TextBlock
        {
            Text = "Checking...",
            Margin = new Thickness(0, 5, 0, 0),
            FontWeight = FontWeights.Bold,
            TextWrapping = TextWrapping.Wrap
        };
        detail = Caption(string.Empty);
        stack.Children.Add(status);
        stack.Children.Add(detail);
        card.Child = stack;
        Grid.SetRow(card, row);
        Grid.SetColumn(card, column);
        return card;
    }

    private TextBlock Caption(string text) => new()
    {
        Text = text,
        Margin = new Thickness(0, 4, 0, 0),
        FontSize = 11.5,
        TextWrapping = TextWrapping.Wrap,
        Foreground = ResourceBrush("Brush.TextTertiary")
    };

    private static Button NewButton(string text, RoutedEventHandler handler)
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

    private async Task LoadModeAsync()
    {
        _uiMode = await _preferencesStore.LoadAsync();
        if (!OperationalDashboardReady) return;
        _modeSelector.SelectedItem = _uiMode;
        ApplyMode();
    }

    private async void ModeSelector_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!OperationalDashboardReady || _modeSelector.SelectedItem is not DowngradeUiMode mode) return;
        _uiMode = mode;
        ApplyMode();
        await _preferencesStore.SaveAsync(mode);
    }

    private void ApplyMode()
    {
        if (!OperationalDashboardReady) return;
        var expert = _uiMode == DowngradeUiMode.Expert;
        _expertPanel.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        DiagnosticsBox.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        LogBox.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        RetryStageButton.Visibility = expert ? Visibility.Visible : Visibility.Collapsed;
        _modeSummary.Text = expert
            ? "Expert mode exposes native logs, targeted retries, exports, profiles, and full health details."
            : "Beginner mode hides technical noise and keeps one required action visible.";
        RefreshOperationalDashboard();
    }

    private void Operational_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot)
    {
        _cableTracker.Observe(snapshot);
        _healthTracker.ObserveDevice(snapshot);
        Dispatcher.BeginInvoke(() =>
        {
            RefreshOperationalDashboard();
            _ = LoadKnownProfileAsync(snapshot);
        });
    }

    private void Operational_TextChanged(object sender, TextChangedEventArgs e) => RefreshOperationalDashboard();
    private void Operational_SelectionChanged(object sender, SelectionChangedEventArgs e) => RefreshOperationalDashboard();

    private void Operational_ProgressChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        _healthTracker.ObserveProgress(InferStage(), OperationProgress.Value);
        RefreshOperationalDashboard();
    }

    private void Operational_LogChanged(object sender, TextChangedEventArgs e)
    {
        _healthTracker.PulseLog();
        if (!OperationalDashboardReady) return;

        var text = LogBox.Text;
        if (text.Length > 2400) text = text[^2400..];
        if (HasFailureSignal(text))
        {
            if (text.Contains("disconnect", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("transfer", StringComparison.OrdinalIgnoreCase) ||
                text.Contains("I/O", StringComparison.OrdinalIgnoreCase))
            {
                _cableTracker.RecordTransferError();
            }
            var guidance = DowngradeFailureTranslator.Translate(text, InferStage());
            _failure.Text = guidance.DisplayText;
            _failure.Foreground = ResourceBrush("Brush.Danger");
        }
    }

    private async void Operational_PostPanelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PostDowngradePanel.Visibility == Visibility.Visible && _completedSession is not null)
        {
            await SaveProfileAndExportAsync(_completedSession, automatic: true);
        }
    }

    private void OperationalTimer_Tick(object? sender, EventArgs e)
    {
        if (!OperationalDashboardReady) return;
        if (_busy && !_operationalRunActive) BeginProtection();
        if (!_busy && _operationalRunActive) EndProtection();
        _healthTracker.ObserveProgress(InferStage(), OperationProgress.Value);
        RefreshOperationalDashboard();
    }

    private void BeginProtection()
    {
        _operationalRunActive = true;
        _powerProtection?.Dispose();
        _powerProtection = new PowerProtectionLease();
        _healthTracker.Start(_monitor.Current);
        WireCloseGuard();
        AppendLog("Operational protection enabled: sleep, display sleep, and accidental app closure are blocked.");
    }

    private void EndProtection()
    {
        _operationalRunActive = false;
        _healthTracker.Stop();
        _powerProtection?.Dispose();
        _powerProtection = null;
    }

    private void WireCloseGuard()
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
            "The app cannot close while a downgrade stage is active. Use Cancel/Pause so DarkSword can preserve the newest safe checkpoint.",
            "Downgrade protection active",
            MessageBoxImage.Warning);
    }

    private void RefreshOperationalDashboard()
    {
        if (!OperationalDashboardReady) return;
        var resources = Path.Combine(_tools.Root, "resources");
        var toolchainReady = _tools.MissingFiles().Count == 0 &&
                             File.Exists(Path.Combine(resources, "sep_racer.bin")) &&
                             File.Exists(Path.Combine(resources, "kpf.bin"));
        var score = _compatibilityService.Assess(
            _detectedDarkSwordDevice,
            _inspection,
            _lastPreflight,
            toolchainReady,
            IsAdministrator());
        _compatibility.Text = score.Summary;
        _compatibility.Foreground = ResourceBrush(score.Rating is "READY" or "GOOD" ? "Brush.Success" : score.Rating == "UNSUPPORTED" ? "Brush.Danger" : "Brush.Accent");
        _compatibilityDetail.Text = string.Join(" ", score.Reasons.Take(_uiMode == DowngradeUiMode.Expert ? 8 : 2));

        try
        {
            var plan = DowngradeStoragePlanner.Calculate(IpswPathBox.Text, AppContext.BaseDirectory);
            _storage.Text = (plan.HasEnoughSpace ? "PASS — " : "BLOCKED — ") + plan.Summary;
            _storage.Foreground = ResourceBrush(plan.HasEnoughSpace ? "Brush.Success" : "Brush.Danger");
            _storageDetail.Text = _uiMode == DowngradeUiMode.Expert
                ? plan.Details.Replace(Environment.NewLine, " • ")
                : "Includes firmware, extraction, cache, session assets, logs, and safety margin.";
        }
        catch (Exception exception)
        {
            _storage.Text = "Storage calculation failed";
            _storageDetail.Detail(exception.Message, ResourceBrush("Brush.Danger"));
        }

        var cable = _cableTracker.GetSnapshot();
        _cable.Text = cable.Summary;
        _cable.Foreground = ResourceBrush(cable.IsHealthy ? "Brush.Success" : "Brush.Danger");
        _cableDetail.Text = cable.Recommendation;

        var health = _healthTracker.GetSnapshot();
        _health.Text = health.State;
        _health.Foreground = ResourceBrush(health.State switch
        {
            "HEALTHY" => "Brush.Success",
            "WARNING" => "Brush.Accent",
            "CRITICAL" => "Brush.Danger",
            _ => "Brush.TextTertiary"
        });
        _healthDetail.Text = health.ActiveTools.Count == 0
            ? health.Summary
            : $"{health.Summary} Active tools: {string.Join(", ", health.ActiveTools)}.";

        _profile.Text = _activeDeviceProfile is null ? "No saved configuration loaded" : $"Loaded {_activeDeviceProfile.ProductType} profile";
        _profileDetail.Text = _activeDeviceProfile is null
            ? "A profile is saved automatically after a successful downgrade."
            : $"Last target {_activeDeviceProfile.LastVersion ?? "unknown"}; updated {_activeDeviceProfile.UpdatedAt.ToLocalTime():g}.";

        _power.Text = _operationalRunActive ? "ACTIVE — sleep and close protection enabled" : "Standby — activates automatically";
        _power.Foreground = ResourceBrush(_operationalRunActive ? "Brush.Success" : "Brush.TextTertiary");

        var advice = ModeRecoveryAdvisor.GetAdvice(_monitor.Current, InferStage());
        _nextAction.Text = $"{advice.Title}: {advice.Action}";
        ConfigureNextAction();
        _exportButton.IsEnabled = _completedSession is not null || _recoveryCandidate?.Session is not null;
    }

    private void ConfigureNextAction()
    {
        if (!OperationalDashboardReady) return;
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

    private async void NextAction_Click(object sender, RoutedEventArgs e)
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
                if (File.Exists(IpswPathBox.Text)) await RunPreflightAsync(showResultDialog: true);
                else RefreshFirmware_Click(RefreshFirmwareButton, new RoutedEventArgs());
                break;
            default:
                RefreshFirmware_Click(RefreshFirmwareButton, new RoutedEventArgs());
                break;
        }
    }

    private async Task LoadKnownProfileAsync(AppleDeviceSnapshot snapshot)
    {
        if (!OperationalDashboardReady) return;
        await Task.Delay(500);
        if (!OperationalDashboardReady || _disposed) return;

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
                IpswSummaryText.Text = "Previously used exact-device firmware loaded. Inspect it before preflight.";
            }
            if (!File.Exists(PtePathBox.Text) && File.Exists(_activeDeviceProfile.LastPteBlockPath))
                PtePathBox.Text = _activeDeviceProfile.LastPteBlockPath!;
        }
        RefreshOperationalDashboard();
    }

    private async Task SaveProfileAndExportAsync(RestoreSession session, bool automatic)
    {
        if (automatic && string.Equals(_lastExportedSessionId, session.SessionId, StringComparison.Ordinal)) return;
        if (automatic) _lastExportedSessionId = session.SessionId;
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
            var export = await _exportService.ExportAsync(session, _logPath, profile, _cableTracker.GetSnapshot());
            _lastExportedSessionId = session.SessionId;
            if (OperationalDashboardControlsReady)
            {
                _profile.Text = "Profile and portable session ZIP saved";
                _profileDetail.Text = export;
            }
            AppendLog($"Saved device profile and portable session export: {export}");
        }
        catch (Exception exception)
        {
            if (OperationalDashboardControlsReady)
            {
                _profile.Text = "Session export failed";
                _profileDetail.Text = exception.Message;
            }
            AppendLog($"Session export failed: {exception}");
        }
    }

    private async void ExportLatest_Click(object sender, RoutedEventArgs e)
    {
        var session = _completedSession ?? _recoveryCandidate?.Session ?? await FindLatestSessionAsync();
        if (session is null)
        {
            ShowMessage("No downgrade session is available to export.", "Session export", MessageBoxImage.Information);
            return;
        }
        await SaveProfileAndExportAsync(session, automatic: false);
        ShowMessage("The portable recovery ZIP and device profile were saved.", "Session exported", MessageBoxImage.Information);
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
    private void Recalculate_Click(object sender, RoutedEventArgs e) => RefreshOperationalDashboard();

    private RestoreStage InferStage()
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

    private static bool HasFailureSignal(string text) =>
        text.Contains("error", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("failed", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("exited with code", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("cancelled", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("disconnected", StringComparison.OrdinalIgnoreCase);

    private void Operational_Unloaded(object sender, RoutedEventArgs e)
    {
        _operationalInitializationQueued = false;
        if (!_operationalExperienceInitialized) return;
        _operationalExperienceInitialized = false;
        _monitor.DeviceChanged -= Operational_DeviceChanged;
        IpswPathBox.TextChanged -= Operational_TextChanged;
        FirmwareList.SelectionChanged -= Operational_SelectionChanged;
        OperationProgress.ValueChanged -= Operational_ProgressChanged;
        LogBox.TextChanged -= Operational_LogChanged;
        PostDowngradePanel.IsVisibleChanged -= Operational_PostPanelChanged;
        Unloaded -= Operational_Unloaded;
        if (_operationalTimer is not null)
        {
            _operationalTimer.Stop();
            _operationalTimer.Tick -= OperationalTimer_Tick;
            _operationalTimer = null;
        }
        EndProtection();
        if (_windowCloseGuardWired && Window.GetWindow(this) is { } owner)
        {
            owner.Closing -= OperationalWindow_Closing;
            _windowCloseGuardWired = false;
        }
    }
}

internal static class OperationalTextBlockExtensions
{
    public static void Detail(this TextBlock block, string text, Brush foreground)
    {
        block.Text = text;
        block.Foreground = foreground;
    }
}
