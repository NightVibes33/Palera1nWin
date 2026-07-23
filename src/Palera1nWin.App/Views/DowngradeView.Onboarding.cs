using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _workflowOnboardingInjected;
    private TextBlock? _downgradeGuideStatus;
    private Border? _coldBootOnboardingCard;
    private TextBlock? _coldBootOnboardingState;
    private TextBlock? _coldBootOnboardingLocation;
    private Button? _openColdBootFolderButton;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(InjectWorkflowOnboarding));
    }

    private void InjectWorkflowOnboarding()
    {
        if (_workflowOnboardingInjected) return;
        if (Content is not ScrollViewer scroll || scroll.Content is not StackPanel root) return;
        _workflowOnboardingInjected = true;

        root.Children.Insert(Math.Min(2, root.Children.Count), BuildDowngradeGuideCard());

        _coldBootOnboardingCard = BuildColdBootGuideCard();
        var postPanelIndex = root.Children.IndexOf(PostDowngradePanel);
        root.Children.Insert(postPanelIndex >= 0 ? postPanelIndex : root.Children.Count, _coldBootOnboardingCard);

        PtePathBox.TextChanged += (_, _) => RefreshColdBootOnboarding();
        PostDowngradePanel.IsVisibleChanged += (_, _) => RefreshColdBootOnboarding();
        _monitor.DeviceChanged += (_, _) => Dispatcher.BeginInvoke(RefreshColdBootOnboarding);

        RefreshDowngradeGuideStatus();
        RefreshColdBootOnboarding();
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(ApplyQuickActionContrast));
    }

    private Border BuildDowngradeGuideCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Before the first downgrade",
            FontSize = 17,
            FontWeight = FontWeights.SemiBold,
            Foreground = OnboardingBrush("Brush.Text", Brushes.White),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Normal path: enter clean DFU, press Start Downgrade, select the iOS 15 IPSW, and approve one final erase confirmation.",
            Margin = new Thickness(0, 5, 0, 10),
            Foreground = OnboardingBrush("Brush.TextSecondary", Brushes.LightGray),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        panel.Children.Add(CreateDowngradeStep("1", "Back up files, photos, authenticator recovery codes, and the Apple ID used by Activation Lock."));
        panel.Children.Add(CreateDowngradeStep("2", "Test DFU → Pwned/Pongo is optional and non-destructive. Start Downgrade runs it automatically when needed."));
        panel.Children.Add(CreateDowngradeStep("3", "Boot Device and Import Boot Profile are used only after a completed downgrade."));

        _downgradeGuideStatus = new TextBlock
        {
            Margin = new Thickness(0, 8, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        panel.Children.Add(_downgradeGuideStatus);

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var open = CreateOnboardingButton("Open Full Guide", true);
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

        return CreateOnboardingCard(
            panel,
            SimpleVisibleTag,
            OnboardingBrush("Brush.Accent", Brushes.Aquamarine));
    }

    private Border BuildColdBootGuideCard()
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Cold Boot Required After Downgrade",
            FontSize = 19,
            FontWeight = FontWeights.Bold,
            Foreground = OnboardingBrush("Brush.Warning", new SolidColorBrush(Color.FromRgb(251, 191, 36))),
        });
        panel.Children.Add(new TextBlock
        {
            Text = "After every shutdown, restart, or dead battery: connect the exact device, enter DFU, and press Boot Device. This does not repeat the downgrade.",
            Margin = new Thickness(0, 6, 0, 12),
            Foreground = OnboardingBrush("Brush.Text", Brushes.White),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        panel.Children.Add(CreateDowngradeStep("1", "Keep the complete session folder containing boot-profile.json, the PTE, and metadata."));
        panel.Children.Add(CreateDowngradeStep("2", "Import Boot Profile only when the matching profile is not auto-loaded."));
        panel.Children.Add(CreateDowngradeStep("3", "Enter DFU with a completely black screen, then press Boot Device."));

        _coldBootOnboardingState = new TextBlock
        {
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, 10, 0, 0),
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

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 0) };
        var openGuide = CreateOnboardingButton("Open Cold Boot Guide", true);
        openGuide.Click += (_, _) => OnboardingWindow.ShowFor(this, OnboardingSection.ColdBoot);
        _openColdBootFolderButton = CreateOnboardingButton("Open Session Folder", false);
        _openColdBootFolderButton.Margin = new Thickness(8, 0, 0, 0);
        _openColdBootFolderButton.Click += (_, _) => OpenColdBootSessionFolder();
        var copy = CreateOnboardingButton("Copy Instructions", false);
        copy.Margin = new Thickness(8, 0, 0, 0);
        copy.Click += (_, _) => CopyColdBootInstructions();
        buttons.Children.Add(openGuide);
        buttons.Children.Add(_openColdBootFolderButton);
        buttons.Children.Add(copy);
        panel.Children.Add(buttons);

        return CreateOnboardingCard(
            panel,
            SimpleVisibleTag,
            OnboardingBrush("Brush.Warning", new SolidColorBrush(Color.FromRgb(251, 191, 36))));
    }

    private UIElement CreateDowngradeStep(string number, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 7) };
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
            Foreground = OnboardingBrush("Brush.Text", Brushes.White),
            TextWrapping = TextWrapping.Wrap,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(detail, 1);
        row.Children.Add(detail);
        return row;
    }

    private Border CreateOnboardingCard(StackPanel panel, string tag, Brush accent)
    {
        var card = new Border
        {
            Tag = tag,
            BorderBrush = accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 16, 0, 0),
            Child = panel,
        };
        ProgrammaticTheme.ApplyCard(this, card);
        card.BorderBrush = accent;
        return card;
    }

    private Button CreateOnboardingButton(string content, bool primary)
    {
        return new Button
        {
            Content = content,
            Padding = new Thickness(14, 7, 14, 7),
            Background = primary
                ? OnboardingBrush("Brush.Accent", Brushes.Aquamarine)
                : OnboardingBrush("Brush.SurfaceTertiary", new SolidColorBrush(Color.FromRgb(35, 42, 58))),
            Foreground = primary ? Brushes.Black : OnboardingBrush("Brush.Text", Brushes.White),
            BorderBrush = OnboardingBrush("Brush.Border", Brushes.DimGray),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
        };
    }

    private Brush OnboardingBrush(string key, Brush fallback) => ProgrammaticTheme.Brush(this, key, fallback);

    private void ApplyQuickActionContrast()
    {
        if (Content is not ScrollViewer scroll || scroll.Content is not StackPanel root) return;

        foreach (var element in root.Children.OfType<FrameworkElement>())
        {
            if (!string.Equals(element.Tag as string, SimpleVisibleTag, StringComparison.Ordinal)) continue;
            if (element is Border card)
            {
                ProgrammaticTheme.ApplyCard(this, card);
                if (ReferenceEquals(card, _coldBootOnboardingCard))
                {
                    card.BorderBrush = OnboardingBrush("Brush.Warning", new SolidColorBrush(Color.FromRgb(251, 191, 36)));
                }
            }
            else
            {
                ProgrammaticTheme.ApplyTextContrast(this, element);
            }
        }

        var primaryText = OnboardingBrush("Brush.Text", Brushes.White);
        var secondaryText = OnboardingBrush("Brush.TextSecondary", Brushes.LightGray);
        if (_simpleDeviceText is not null) _simpleDeviceText.Foreground = secondaryText;
        if (_simpleFirmwareText is not null) _simpleFirmwareText.Foreground = secondaryText;
        if (_simpleStageText is not null) _simpleStageText.Foreground = primaryText;
        if (_simpleProgress is not null)
        {
            _simpleProgress.Foreground = OnboardingBrush("Brush.Accent", Brushes.Aquamarine);
            _simpleProgress.Background = OnboardingBrush("Brush.SurfaceTertiary", Brushes.DimGray);
        }
        if (_simpleStartButton is not null) _simpleStartButton.Foreground = Brushes.Black;
        if (_simpleBootButton is not null) _simpleBootButton.Foreground = Brushes.Black;
        if (_simpleTestButton is not null) _simpleTestButton.Foreground = primaryText;
        if (_simpleImportButton is not null) _simpleImportButton.Foreground = primaryText;
    }

    private void RefreshDowngradeGuideStatus()
    {
        if (_downgradeGuideStatus is null) return;
        var complete = OnboardingStateStore.Load().DowngradeGuideCompleted;
        _downgradeGuideStatus.Text = complete
            ? "Guide status: Read. You can reopen it at any time."
            : "Guide status: Review recommended before the first erase.";
        _downgradeGuideStatus.Foreground = complete
            ? OnboardingBrush("Brush.Success", Brushes.LightGreen)
            : OnboardingBrush("Brush.Accent", Brushes.Aquamarine);
    }

    private void RefreshColdBootOnboarding()
    {
        if (_coldBootOnboardingCard is null || _coldBootOnboardingState is null || _coldBootOnboardingLocation is null) return;

        _coldBootOnboardingCard.Visibility = _activeBootProfile is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        if (_activeBootProfile is null)
        {
            _coldBootOnboardingState.Text = "No boot profile is loaded yet.";
            _coldBootOnboardingLocation.Text = string.Empty;
            if (_openColdBootFolderButton is not null)
            {
                _openColdBootFolderButton.IsEnabled = Directory.Exists(_bootProfileStore.RootDirectory);
            }
            return;
        }

        var profilePath = Path.Combine(_activeBootProfile.SessionDirectory, "boot-profile.json");
        _coldBootOnboardingState.Text =
            $"READY — {_activeBootProfile.ProductType} {_activeBootProfile.TargetVersion} ({_activeBootProfile.TargetBuild})";
        _coldBootOnboardingState.Foreground = OnboardingBrush("Brush.Success", Brushes.LightGreen);
        _coldBootOnboardingLocation.Text =
            $"Required profile: {profilePath}\n" +
            $"Back up this entire folder: {_activeBootProfile.SessionDirectory}";
        if (_openColdBootFolderButton is not null)
        {
            _openColdBootFolderButton.IsEnabled = Directory.Exists(_activeBootProfile.SessionDirectory);
        }
        ApplyQuickActionContrast();
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
            "5. Open Downgrade and press Boot Device.\n" +
            "6. Do not select a raw PTE or edit boot-profile.json.\n");
        ShowMessage("Cold Boot instructions were copied to the clipboard.", "Instructions copied", MessageBoxImage.Information);
    }
}