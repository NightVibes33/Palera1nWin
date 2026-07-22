using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _workflowOnboardingInjected;
    private TextBlock? _downgradeGuideStatus;
    private TextBlock? _coldBootOnboardingState;
    private TextBlock? _coldBootOnboardingLocation;
    private Button? _openColdBootFolderButton;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(InjectWorkflowOnboarding));
    }

    private void InjectWorkflowOnboarding()
    {
        if (_workflowOnboardingInjected) return;
        if (Content is not ScrollViewer scroll || scroll.Content is not StackPanel root) return;
        _workflowOnboardingInjected = true;

        root.Children.Insert(Math.Min(2, root.Children.Count), BuildDowngradeGuideCard());

        var coldBootCard = BuildColdBootGuideCard();
        var postPanelIndex = root.Children.IndexOf(PostDowngradePanel);
        root.Children.Insert(postPanelIndex >= 0 ? postPanelIndex : root.Children.Count, coldBootCard);

        PtePathBox.TextChanged += (_, _) => RefreshColdBootOnboarding();
        PostDowngradePanel.IsVisibleChanged += (_, _) => RefreshColdBootOnboarding();
        _monitor.DeviceChanged += (_, _) => Dispatcher.BeginInvoke(RefreshColdBootOnboarding);
        RefreshDowngradeGuideStatus();
        RefreshColdBootOnboarding();
    }

    private Border BuildDowngradeGuideCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Downgrade onboarding",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "This is a destructive, tethered restore. The app will not enable the full downgrade until the exact-device IPSW, confirmations, and non-destructive DFU → PongoOS test are valid.",
            Margin = new Thickness(0, 5, 0, 12),
            Foreground = OnboardingBrush("Brush.TextSecondary", Brushes.LightGray),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        panel.Children.Add(CreateDowngradeStep("1", "Back up the device, files, photos, recovery codes, and Activation Lock credentials."));
        panel.Children.Add(CreateDowngradeStep("2", "Connect the exact device and select an iOS/iPadOS 15 IPSW that contains its ProductType."));
        panel.Children.Add(CreateDowngradeStep("3", "Run Test DFU → PongoOS before any erase. This proves checkm8, drivers, PongoOS, and bridge access."));
        panel.Children.Add(CreateDowngradeStep("4", "Run preflight, type the exact ProductType, and confirm the erase plus tethered-boot requirement."));
        panel.Children.Add(CreateDowngradeStep("5", "After completion, back up the entire session folder containing boot-profile.json and its PTE."));

        _downgradeGuideStatus = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_downgradeGuideStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var open = CreateOnboardingButton("Open Full Downgrade Guide", true);
        open.Click += (_, _) => OnboardingWindow.ShowFor(this, OnboardingSection.Downgrade);
        var mark = CreateOnboardingButton("Mark Guide Read", false);
        mark.Margin = new Thickness(8, 0, 0, 0);
        mark.Click += (_, _) =>
        {
            var state = OnboardingStateStore.Load();
            OnboardingStateStore.MarkSectionComplete(state, OnboardingSection.Downgrade);
            RefreshDowngradeGuideStatus();
        };
        buttons.Children.Add(open);
        buttons.Children.Add(mark);
        panel.Children.Add(buttons);

        return CreateOnboardingCard(panel, "downgrade-onboarding", OnboardingBrush("Brush.Accent", Brushes.Aquamarine));
    }

    private Border BuildColdBootGuideCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Cold Boot Required After Downgrade",
            FontSize = 20,
            FontWeight = FontWeights.Bold,
            Foreground = OnboardingBrush("Brush.Warning", new SolidColorBrush(Color.FromRgb(255, 194, 92))),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "After every shutdown, restart, or dead battery: connect this exact device to Windows, enter DFU, and press Boot Device. This does not repeat the downgrade.",
            Margin = new Thickness(0, 6, 0, 14),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });

        panel.Children.Add(CreateDowngradeStep("1", "Required file: boot-profile.json from the completed DarkSword session. Raw PTE .bin selection is blocked."));
        panel.Children.Add(CreateDowngradeStep("2", "Keep the complete session folder together; boot-profile.json references the device-specific PTE and verified metadata."));
        panel.Children.Add(CreateDowngradeStep("3", "Enter DFU with a completely black screen, then press Boot Device."));
        panel.Children.Add(CreateDowngradeStep("4", "The app rechecks ProductType, ECID, PTE, SEP, and KPF hashes before sending any payload."));

        _coldBootOnboardingState = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 12, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        _coldBootOnboardingLocation = new TextBlock
        {
            Foreground = OnboardingBrush("Brush.TextSecondary", Brushes.LightGray),
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_coldBootOnboardingState);
        panel.Children.Add(_coldBootOnboardingLocation);

        var buttons = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        var openGuide = CreateOnboardingButton("Open Full Cold Boot Guide", true);
        openGuide.Click += (_, _) => OnboardingWindow.ShowFor(this, OnboardingSection.ColdBoot);
        _openColdBootFolderButton = CreateOnboardingButton("Open Session Folder", false);
        _openColdBootFolderButton.Margin = new Thickness(8, 0, 0, 0);
        _openColdBootFolderButton.Click += (_, _) => OpenColdBootSessionFolder();
        var copy = CreateOnboardingButton("Copy Boot Instructions", false);
        copy.Margin = new Thickness(8, 0, 0, 0);
        copy.Click += (_, _) => CopyColdBootInstructions();
        var mark = CreateOnboardingButton("Mark Guide Read", false);
        mark.Margin = new Thickness(8, 0, 0, 0);
        mark.Click += (_, _) =>
        {
            var state = OnboardingStateStore.Load();
            OnboardingStateStore.MarkSectionComplete(state, OnboardingSection.ColdBoot);
            RefreshColdBootOnboarding();
        };
        buttons.Children.Add(openGuide);
        buttons.Children.Add(_openColdBootFolderButton);
        buttons.Children.Add(copy);
        buttons.Children.Add(mark);
        panel.Children.Add(buttons);

        return CreateOnboardingCard(
            panel,
            "cold-boot-onboarding",
            OnboardingBrush("Brush.Warning", new SolidColorBrush(Color.FromRgb(255, 194, 92))));
    }

    private UIElement CreateDowngradeStep(string number, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = OnboardingBrush("Brush.Accent", Brushes.Aquamarine),
            Child = new TextBlock
            {
                Text = number,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                FontSize = 11,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        row.Children.Add(badge);
        var detail = new TextBlock
        {
            Text = text,
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        return row;
    }

    private Border CreateOnboardingCard(StackPanel panel, string tag, Brush accent) => new()
    {
        Tag = tag,
        Background = OnboardingBrush("ControlFillColorSecondaryBrush", new SolidColorBrush(Color.FromRgb(35, 40, 48))),
        BorderBrush = accent,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 18, 0, 0),
        Child = panel,
    };

    private Button CreateOnboardingButton(string content, bool primary) => new()
    {
        Content = content,
        Padding = new Thickness(14, 7, 14, 7),
        Background = primary
            ? OnboardingBrush("Brush.Accent", Brushes.Aquamarine)
            : OnboardingBrush("ControlFillColorSecondaryBrush", new SolidColorBrush(Color.FromRgb(35, 40, 48))),
        Foreground = primary ? Brushes.Black : Brushes.White,
        BorderBrush = OnboardingBrush("ControlStrokeColorDefaultBrush", Brushes.DimGray),
        BorderThickness = new Thickness(1),
        FontWeight = FontWeights.SemiBold,
    };

    private Brush OnboardingBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void RefreshDowngradeGuideStatus()
    {
        if (_downgradeGuideStatus is null) return;
        var complete = OnboardingStateStore.Load().DowngradeGuideCompleted;
        _downgradeGuideStatus.Text = complete
            ? "Guide status: Read. The destructive restore remains protected by hardware validation and confirmations."
            : "Guide status: Not marked read. Review it before the first downgrade attempt.";
        _downgradeGuideStatus.Foreground = complete
            ? OnboardingBrush("Brush.Success", Brushes.LightGreen)
            : OnboardingBrush("Brush.Accent", Brushes.Aquamarine);
    }

    private void RefreshColdBootOnboarding()
    {
        if (_coldBootOnboardingState is null || _coldBootOnboardingLocation is null) return;
        var guideComplete = OnboardingStateStore.Load().ColdBootGuideCompleted;
        if (_activeBootProfile is null)
        {
            _coldBootOnboardingState.Text = "No exact-device boot profile is loaded yet.";
            _coldBootOnboardingState.Foreground = OnboardingBrush("Brush.Warning", new SolidColorBrush(Color.FromRgb(255, 194, 92)));
            _coldBootOnboardingLocation.Text =
                "After a successful downgrade, DarkSword saves boot-profile.json inside the session folder and auto-loads it for the matching ECID. You can also use Import Profile.";
            if (_openColdBootFolderButton is not null) _openColdBootFolderButton.IsEnabled = Directory.Exists(_bootProfileStore.RootDirectory);
            return;
        }

        var profilePath = Path.Combine(_activeBootProfile.SessionDirectory, "boot-profile.json");
        _coldBootOnboardingState.Text =
            $"READY — {_activeBootProfile.ProductType} {_activeBootProfile.TargetVersion} ({_activeBootProfile.TargetBuild}). " +
            $"Cold Boot guide: {(guideComplete ? "Read" : "Not marked read")}.";
        _coldBootOnboardingState.Foreground = OnboardingBrush("Brush.Success", Brushes.LightGreen);
        _coldBootOnboardingLocation.Text =
            $"Required profile: {profilePath}\n" +
            $"Back up this entire folder: {_activeBootProfile.SessionDirectory}";
        if (_openColdBootFolderButton is not null) _openColdBootFolderButton.IsEnabled = Directory.Exists(_activeBootProfile.SessionDirectory);
    }

    private void OpenColdBootSessionFolder()
    {
        var directory = _activeBootProfile?.SessionDirectory;
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            directory = Directory.Exists(_bootProfileStore.RootDirectory)
                ? _bootProfileStore.RootDirectory
                : _dataDirectory;
        }

        Directory.CreateDirectory(directory);
        Process.Start(new ProcessStartInfo(directory) { UseShellExecute = true });
    }

    private void CopyColdBootInstructions()
    {
        var profilePath = _activeBootProfile is null
            ? "Import boot-profile.json from the completed DarkSword session"
            : Path.Combine(_activeBootProfile.SessionDirectory, "boot-profile.json");
        var sessionPath = _activeBootProfile?.SessionDirectory ?? "the completed DarkSword session folder";
        Clipboard.SetText(
            "DARKSWORD COLD BOOT\n" +
            "Required after every shutdown, restart, or dead battery.\n\n" +
            $"1. Keep the complete session folder backed up: {sessionPath}\n" +
            $"2. Required file: {profilePath}\n" +
            "3. Open Palera1nWin and connect the exact downgraded device.\n" +
            "4. Enter DFU mode; the screen must remain completely black.\n" +
            "5. Open the Downgrade tab and press Boot Device.\n" +
            "6. Do not select a raw PTE .bin or edit boot-profile.json.\n");
        ShowMessage("Cold Boot instructions were copied to the clipboard.", "Instructions copied", MessageBoxImage.Information);
    }
}
