using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public sealed class OnboardingWindow : Window
{
    private static readonly Brush WindowBrush = new SolidColorBrush(Color.FromRgb(13, 16, 23));
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(21, 26, 36));
    private static readonly Brush PanelAltBrush = new SolidColorBrush(Color.FromRgb(27, 33, 48));
    private static readonly Brush StrokeBrush = new SolidColorBrush(Color.FromRgb(53, 64, 87));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(244, 247, 251));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(193, 201, 216));
    private static readonly Brush TertiaryBrush = new SolidColorBrush(Color.FromRgb(154, 165, 184));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(45, 212, 191));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(251, 191, 36));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));

    private static readonly OnboardingSection[] SectionOrder =
    {
        OnboardingSection.Overview,
        OnboardingSection.Jailbreak,
        OnboardingSection.Downgrade,
        OnboardingSection.ColdBoot,
    };

    private readonly OnboardingState _state;
    private readonly Action<int>? _navigate;
    private readonly StackPanel _sectionContent = new();
    private readonly TextBlock _sectionTitle = new();
    private readonly TextBlock _sectionSubtitle = new();
    private readonly TextBlock _sectionProgress = new();
    private readonly CheckBox _sectionComplete = new();
    private readonly CheckBox _hideAutomatic = new();
    private readonly Button _openWorkflowButton = new();
    private readonly Button _previousButton = new();
    private readonly Button _nextButton = new();
    private readonly Dictionary<OnboardingSection, Button> _navigationButtons = new();
    private OnboardingSection _section;

    public OnboardingWindow(
        OnboardingState state,
        Action<int>? navigate,
        OnboardingSection initialSection = OnboardingSection.Overview)
    {
        _state = state;
        _navigate = navigate;
        _section = initialSection;

        Title = "Palera1nWin Setup Guide";
        Width = 940;
        Height = 780;
        MinWidth = 760;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = WindowBrush;
        Foreground = TextBrush;

        Content = BuildLayout();
        TextElement.SetForeground(this, TextBrush);
        Loaded += (_, _) =>
        {
            OnboardingStateStore.MarkViewed(_state);
            ShowSection(_section);
        };
    }

    public static void ShowFor(DependencyObject source, OnboardingSection section)
    {
        var owner = Window.GetWindow(source);
        Action<int>? navigate = owner is MainWindow main ? main.NavigateToWorkflow : null;
        var window = new OnboardingWindow(OnboardingStateStore.Load(), navigate, section)
        {
            Owner = owner,
        };
        window.ShowDialog();
    }

    public static void ShowFirstRun(MainWindow owner)
    {
        if (!OnboardingStateStore.ShouldShowAutomatically()) return;
        var window = new OnboardingWindow(
            OnboardingStateStore.Load(),
            owner.NavigateToWorkflow,
            OnboardingSection.Overview)
        {
            Owner = owner,
        };
        window.ShowDialog();
    }

    private UIElement BuildLayout()
    {
        var root = new Grid { Background = WindowBrush };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new Border
        {
            Background = PanelBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(28, 22, 28, 18),
            Child = new StackPanel
            {
                Children =
                {
                    new TextBlock
                    {
                        Text = "Palera1nWin onboarding",
                        FontSize = 28,
                        FontWeight = FontWeights.Bold,
                        Foreground = TextBrush,
                    },
                    new TextBlock
                    {
                        Text = "Choose one goal, follow the matching workflow, and know what must be repeated after a reboot.",
                        FontSize = 14,
                        Foreground = MutedBrush,
                        Margin = new Thickness(0, 7, 0, 0),
                        TextWrapping = TextWrapping.Wrap,
                    },
                },
            },
        };
        Grid.SetRow(header, 0);
        root.Children.Add(header);

        var navigationGrid = new Grid { Margin = new Thickness(24, 16, 24, 12) };
        navigationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        navigationGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var navigation = new WrapPanel();
        AddNavigationButton(navigation, "Overview", OnboardingSection.Overview);
        AddNavigationButton(navigation, "Jailbreak", OnboardingSection.Jailbreak);
        AddNavigationButton(navigation, "Downgrade", OnboardingSection.Downgrade);
        AddNavigationButton(navigation, "Cold Boot", OnboardingSection.ColdBoot);
        navigationGrid.Children.Add(navigation);
        _sectionProgress.Foreground = TertiaryBrush;
        _sectionProgress.FontWeight = FontWeights.SemiBold;
        _sectionProgress.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_sectionProgress, 1);
        navigationGrid.Children.Add(_sectionProgress);
        Grid.SetRow(navigationGrid, 1);
        root.Children.Add(navigationGrid);

        var contentPanel = new StackPanel { Margin = new Thickness(26, 8, 26, 22) };
        _sectionTitle.FontSize = 23;
        _sectionTitle.FontWeight = FontWeights.Bold;
        _sectionTitle.Foreground = TextBrush;
        _sectionSubtitle.FontSize = 13;
        _sectionSubtitle.Foreground = MutedBrush;
        _sectionSubtitle.TextWrapping = TextWrapping.Wrap;
        _sectionSubtitle.Margin = new Thickness(0, 5, 0, 18);
        contentPanel.Children.Add(_sectionTitle);
        contentPanel.Children.Add(_sectionSubtitle);
        contentPanel.Children.Add(_sectionContent);

        var scroll = new ScrollViewer
        {
            Content = contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        };
        Grid.SetRow(scroll, 2);
        root.Children.Add(scroll);

        var footerGrid = new Grid
        {
            Background = PanelBrush,
            Margin = new Thickness(0),
        };
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var footerLeft = new StackPanel { Margin = new Thickness(24, 14, 16, 14) };
        _sectionComplete.Foreground = TextBrush;
        _sectionComplete.Margin = new Thickness(0, 0, 0, 7);
        _sectionComplete.Checked += (_, _) => SaveSectionCompletion();
        _hideAutomatic.Content = "I understand these workflows; do not open this guide automatically again";
        _hideAutomatic.Foreground = MutedBrush;
        footerLeft.Children.Add(_sectionComplete);
        footerLeft.Children.Add(_hideAutomatic);
        footerGrid.Children.Add(footerLeft);

        var footerButtons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 12, 24, 12),
        };
        _previousButton.Content = "Back";
        _previousButton.Padding = new Thickness(16, 9, 16, 9);
        _previousButton.Margin = new Thickness(0, 0, 8, 0);
        _previousButton.Click += (_, _) => MoveSection(-1);
        StyleButton(_previousButton, primary: false);

        _nextButton.Padding = new Thickness(16, 9, 16, 9);
        _nextButton.Margin = new Thickness(0, 0, 8, 0);
        _nextButton.Click += (_, _) => MoveSection(1);
        StyleButton(_nextButton, primary: false);

        _openWorkflowButton.Padding = new Thickness(18, 9, 18, 9);
        _openWorkflowButton.Margin = new Thickness(0, 0, 8, 0);
        _openWorkflowButton.Click += (_, _) => OpenCurrentWorkflow();
        StyleButton(_openWorkflowButton, primary: true);

        var closeButton = new Button
        {
            Content = "Close guide",
            Padding = new Thickness(18, 9, 18, 9),
        };
        StyleButton(closeButton, primary: false);
        closeButton.Click += (_, _) => Finish();

        footerButtons.Children.Add(_previousButton);
        footerButtons.Children.Add(_nextButton);
        footerButtons.Children.Add(_openWorkflowButton);
        footerButtons.Children.Add(closeButton);
        Grid.SetColumn(footerButtons, 1);
        footerGrid.Children.Add(footerButtons);
        Grid.SetRow(footerGrid, 3);
        root.Children.Add(footerGrid);

        return root;
    }

    private void AddNavigationButton(Panel navigation, string label, OnboardingSection section)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 9, 0),
            Tag = section,
        };
        button.Click += (_, _) => ShowSection(section);
        _navigationButtons[section] = button;
        navigation.Children.Add(button);
    }

    private void ShowSection(OnboardingSection section)
    {
        _section = section;
        _sectionContent.Children.Clear();

        switch (section)
        {
            case OnboardingSection.Overview:
                RenderOverview();
                break;
            case OnboardingSection.Jailbreak:
                RenderJailbreak();
                break;
            case OnboardingSection.Downgrade:
                RenderDowngrade();
                break;
            case OnboardingSection.ColdBoot:
                RenderColdBoot();
                break;
        }

        foreach (var pair in _navigationButtons)
        {
            StyleButton(pair.Value, primary: pair.Key == section);
        }

        var index = Array.IndexOf(SectionOrder, section);
        _sectionProgress.Text = $"Guide {index + 1} of {SectionOrder.Length}";
        _previousButton.IsEnabled = index > 0;
        _nextButton.Content = index == SectionOrder.Length - 1 ? "Finish" : "Next";

        _sectionComplete.IsEnabled = section != OnboardingSection.Overview;
        _sectionComplete.IsChecked = section switch
        {
            OnboardingSection.Jailbreak => _state.JailbreakGuideCompleted,
            OnboardingSection.Downgrade => _state.DowngradeGuideCompleted,
            OnboardingSection.ColdBoot => _state.ColdBootGuideCompleted,
            _ => false,
        };
        _sectionComplete.Content = section == OnboardingSection.Overview
            ? "Open the guide for the workflow you plan to use"
            : $"I read and understand the {SectionName(section)} guide";

        _openWorkflowButton.Visibility = section == OnboardingSection.Overview
            ? Visibility.Collapsed
            : Visibility.Visible;
        _openWorkflowButton.Content = section == OnboardingSection.Jailbreak
            ? "Open Jailbreak tab"
            : section == OnboardingSection.Downgrade
                ? "Open Downgrade tab"
                : "Open Boot Device";
    }

    private void MoveSection(int direction)
    {
        var index = Array.IndexOf(SectionOrder, _section);
        if (direction > 0 && index == SectionOrder.Length - 1)
        {
            Finish();
            return;
        }

        var next = Math.Clamp(index + direction, 0, SectionOrder.Length - 1);
        ShowSection(SectionOrder[next]);
    }

    private void RenderOverview()
    {
        _sectionTitle.Text = "Choose the workflow that matches your goal";
        _sectionSubtitle.Text = "The three workflows share USB tools, but they do different jobs. Only run one at a time.";
        AddCallout(
            "Fast decision",
            "Keep the installed firmware and add a jailbreak: use Jailbreak. Install iOS/iPadOS 15 and erase the device: use Downgrade. Start an already-downgraded device after power-off: use Boot Device.",
            AccentBrush);
        AddWorkflowCard(
            "Jailbreak",
            "Keeps the firmware currently installed and starts palera1n. A full reboot removes the active jailbreak state, so run the Jailbreak workflow again.",
            "No firmware downgrade and no planned erase.");
        AddWorkflowCard(
            "Downgrade",
            "Erases the device and installs a supported iOS/iPadOS 15 IPSW through the DarkSword restore path. Device model and ECID are detected automatically.",
            "Creates a device-specific session and boot-profile.json.");
        AddWorkflowCard(
            "Cold Boot",
            "Starts a device that was already downgraded. Use Boot Device after every shutdown, restart, or dead battery.",
            "Does not repeat the restore or erase the device.");
        AddCallout(
            "Do not run separate USB tools beside the app",
            "Palera1nWin controls Apple USB ownership, drivers, openra1n, PongoOS, and WSL as one transaction. Close Zadig, gaster, usbipd terminals, and other jailbreak tools while an operation is active.",
            WarningBrush);
    }

    private void RenderJailbreak()
    {
        _sectionTitle.Text = "Jailbreak onboarding";
        _sectionSubtitle.Text = "Use this path to jailbreak the firmware already installed on the device.";
        AddCallout(
            "Before you start",
            "Run Palera1nWin as Administrator, finish Setup, keep WSL available, connect only the target Apple device, and use a reliable data cable connected directly to the PC.",
            AccentBrush);
        AddStep(1, "Choose the jailbreak options", "Rootless is recommended. Rootful uses more storage. Safe Mode disables tweak injection, and Verbose Boot shows detailed boot output.");
        AddStep(2, "Press Start Jailbreak", "The app verifies the packaged runtime, confirms one connected Apple device, and guides the device into DFU.");
        AddStep(3, "Follow the timed DFU instructions", "The screen must remain completely black. The app performs the Windows/WSL USB handoff, verifies drivers, runs openra1n, confirms PongoOS, and starts palera1n.");
        AddStep(4, "Finish on the device", "Open the loader/bootstrap shown after boot. After a full reboot, return to Jailbreak and press Start Jailbreak again.");
        AddCallout(
            "Jailbreak does not use Boot Device",
            "Boot Device is reserved for a DarkSword-downgraded device with a matching boot-profile.json.",
            WarningBrush);
    }

    private void RenderDowngrade()
    {
        _sectionTitle.Text = "Downgrade onboarding";
        _sectionSubtitle.Text = "The visible Downgrade screen has four actions. The app performs device detection and technical checks automatically.";
        AddCallout(
            "The downgrade erases the device",
            "Back up photos and files, save authenticator recovery codes, and know the Apple ID password used by Activation Lock before approving the final erase confirmation.",
            DangerBrush);

        AddButtonExplanation("Start Downgrade", "The normal one-button path. Enter clean DFU, press this button, select the iOS/iPadOS 15 IPSW, and approve one final erase confirmation. The app runs DFU → Pwned/Pongo automatically when needed.", SuccessBrush);
        AddButtonExplanation("Test DFU → Pwned/Pongo", "Optional and non-destructive. Use it to prove the cable, driver, checkm8, PongoOS enumeration, and bridge before starting the erase.", AccentBrush);
        AddButtonExplanation("Boot Device", "Use only after a completed downgrade. It starts the tethered iOS/iPadOS 15 installation after every shutdown or restart.", WarningBrush);
        AddButtonExplanation("Import Boot Profile", "Loads boot-profile.json from a completed DarkSword session when it is not auto-loaded. Do not select a raw PTE file.", TertiaryBrush);

        AddStep(1, "Enter clean DFU", "Connect only the target iPad. DFU has a completely black screen; the recovery cable/computer screen is not DFU.");
        AddStep(2, "Press Start Downgrade and select the IPSW", "The app reads BuildManifest, verifies iOS/iPadOS 15, matches ProductType automatically, records ECID, and checks the IPSW hash.");
        AddStep(3, "Approve the final erase confirmation", "No model name typing or checkbox maze is required. A real wrong-device, wrong-IPSW, missing-file, or integrity mismatch still stops the operation automatically.");
        AddStep(4, "Keep the completed session folder", "After restore, the app creates boot-profile.json plus the device-specific PTE and metadata. Back up the entire folder to another drive.");
        AddCallout(
            "Normal fastest path",
            "Enter DFU → press Start Downgrade → select the correct iOS 15 IPSW → approve the erase → wait. The separate Test button is optional.",
            AccentBrush);
    }

    private void RenderColdBoot()
    {
        _sectionTitle.Text = "Cold Boot onboarding";
        _sectionSubtitle.Text = "Use this after every full shutdown, restart, or dead battery on a DarkSword-downgraded device.";
        AddCallout(
            "Required file: boot-profile.json",
            "The app normally auto-loads the matching profile. Use Import Boot Profile when moving the session to another PC or when automatic discovery does not find it.",
            AccentBrush);
        AddStep(1, "Keep the complete session folder", "boot-profile.json, the PTE file, metadata, sep_racer.bin, and kpf.bin must remain available. Do not edit the JSON.");
        AddStep(2, "Connect the exact downgraded device and enter DFU", "The screen remains completely black. The app rechecks ProductType and ECID before sending boot payloads.");
        AddStep(3, "Press Boot Device", "The app verifies the saved profile and hashes, obtains PongoOS, loads the SEP/PTE/KPF plan, and sends the final boot command.");
        AddStep(4, "Wait for iOS/iPadOS 15 to start", "Leave the cable connected until the device returns. This boot does not erase the device or repeat the downgrade.");
        AddCallout(
            "When Boot Device is blocked",
            "Import the correct boot-profile.json and reconnect the exact device. Never bypass an ECID or hash mismatch by editing the profile or selecting another device's PTE.",
            DangerBrush);
    }

    private void AddWorkflowCard(string title, string body, string footer)
    {
        var panel = new StackPanel();
        panel.Children.Add(PrimaryText(title, 18, FontWeights.SemiBold));
        panel.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 6, 0, 8),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        panel.Children.Add(new TextBlock
        {
            Text = footer,
            Foreground = AccentBrush,
            FontWeight = FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        });
        _sectionContent.Children.Add(CreateCard(panel));
    }

    private void AddButtonExplanation(string title, string detail, Brush accent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        _sectionContent.Children.Add(CreateCard(panel));
    }

    private void AddStep(int number, string title, string detail)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 34,
            Height = 34,
            CornerRadius = new CornerRadius(17),
            Background = AccentBrush,
            VerticalAlignment = VerticalAlignment.Top,
            Child = new TextBlock
            {
                Text = number.ToString(),
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(badge);
        var text = new StackPanel();
        text.Children.Add(PrimaryText(title, 16, FontWeights.SemiBold));
        text.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 5, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        _sectionContent.Children.Add(CreateCard(grid));
    }

    private void AddCallout(string title, string detail, Brush accent)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
            Foreground = accent,
        });
        panel.Children.Add(new TextBlock
        {
            Text = detail,
            Foreground = TextBrush,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        var card = CreateCard(panel);
        card.BorderBrush = accent;
        _sectionContent.Children.Add(card);
    }

    private static TextBlock PrimaryText(string text, double fontSize, FontWeight weight) => new()
    {
        Text = text,
        FontSize = fontSize,
        FontWeight = weight,
        Foreground = TextBrush,
        TextWrapping = TextWrapping.Wrap,
    };

    private static Border CreateCard(UIElement child)
    {
        var card = new Border
        {
            Background = PanelAltBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 0, 0, 12),
            Child = child,
        };
        TextElement.SetForeground(card, TextBrush);
        return card;
    }

    private static void StyleButton(Button button, bool primary)
    {
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = primary ? AccentBrush : StrokeBrush;
        button.Background = primary ? AccentBrush : PanelAltBrush;
        button.Foreground = primary ? Brushes.Black : TextBrush;
        button.FontWeight = FontWeights.SemiBold;
    }

    private void SaveSectionCompletion()
    {
        if (_sectionComplete.IsChecked != true || _section == OnboardingSection.Overview) return;
        OnboardingStateStore.MarkSectionComplete(_state, _section);
    }

    private void Finish()
    {
        if (_hideAutomatic.IsChecked == true)
        {
            OnboardingStateStore.CompleteAll(_state);
        }
        else
        {
            OnboardingStateStore.Save(_state);
        }
        Close();
    }

    private void OpenCurrentWorkflow()
    {
        if (_section is OnboardingSection.Jailbreak or OnboardingSection.Downgrade or OnboardingSection.ColdBoot)
        {
            OnboardingStateStore.MarkSectionComplete(_state, _section);
        }
        _navigate?.Invoke(_section == OnboardingSection.Jailbreak ? 0 : 1);
        Close();
    }

    private static string SectionName(OnboardingSection section) => section switch
    {
        OnboardingSection.Jailbreak => "Jailbreak",
        OnboardingSection.Downgrade => "Downgrade",
        OnboardingSection.ColdBoot => "Cold Boot",
        _ => "Overview",
    };
}