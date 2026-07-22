using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public sealed class OnboardingWindow : Window
{
    private static readonly Brush WindowBrush = new SolidColorBrush(Color.FromRgb(18, 20, 25));
    private static readonly Brush PanelBrush = new SolidColorBrush(Color.FromRgb(29, 33, 41));
    private static readonly Brush PanelAltBrush = new SolidColorBrush(Color.FromRgb(37, 42, 52));
    private static readonly Brush StrokeBrush = new SolidColorBrush(Color.FromRgb(58, 66, 80));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(174, 181, 193));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(67, 210, 194));
    private static readonly Brush WarningBrush = new SolidColorBrush(Color.FromRgb(255, 194, 92));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(255, 116, 116));

    private readonly OnboardingState _state;
    private readonly Action<int>? _navigate;
    private readonly StackPanel _sectionContent = new();
    private readonly TextBlock _sectionTitle = new();
    private readonly TextBlock _sectionSubtitle = new();
    private readonly CheckBox _sectionComplete = new();
    private readonly CheckBox _hideAutomatic = new();
    private readonly Button _openWorkflowButton = new();
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
        Width = 920;
        Height = 760;
        MinWidth = 760;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = WindowBrush;
        Foreground = Brushes.White;

        Content = BuildLayout();
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
        var root = new Grid();
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
                    },
                    new TextBlock
                    {
                        Text = "Choose the correct workflow, understand what changes, and know exactly what to do after a reboot or downgrade.",
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

        var navigation = new WrapPanel
        {
            Margin = new Thickness(24, 16, 24, 12),
        };
        navigation.Children.Add(CreateNavButton("Overview", OnboardingSection.Overview));
        navigation.Children.Add(CreateNavButton("Jailbreak", OnboardingSection.Jailbreak));
        navigation.Children.Add(CreateNavButton("Downgrade", OnboardingSection.Downgrade));
        navigation.Children.Add(CreateNavButton("Cold Boot", OnboardingSection.ColdBoot));
        Grid.SetRow(navigation, 1);
        root.Children.Add(navigation);

        var contentPanel = new StackPanel
        {
            Margin = new Thickness(26, 8, 26, 22),
        };
        _sectionTitle.FontSize = 23;
        _sectionTitle.FontWeight = FontWeights.Bold;
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
        var footerLeft = new StackPanel
        {
            Margin = new Thickness(24, 14, 16, 14),
        };
        _sectionComplete.Foreground = Brushes.White;
        _sectionComplete.Margin = new Thickness(0, 0, 0, 7);
        _sectionComplete.Checked += (_, _) => SaveSectionCompletion();
        _hideAutomatic.Content = "I understand all three workflows; do not open this guide automatically again";
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
        _openWorkflowButton.Padding = new Thickness(18, 9, 18, 9);
        _openWorkflowButton.Margin = new Thickness(0, 0, 10, 0);
        _openWorkflowButton.Click += (_, _) => OpenCurrentWorkflow();
        StyleButton(_openWorkflowButton, primary: true);
        var closeButton = new Button
        {
            Content = "Finish",
            Padding = new Thickness(18, 9, 18, 9),
        };
        StyleButton(closeButton, primary: false);
        closeButton.Click += (_, _) => Finish();
        footerButtons.Children.Add(_openWorkflowButton);
        footerButtons.Children.Add(closeButton);
        Grid.SetColumn(footerButtons, 1);
        footerGrid.Children.Add(footerButtons);
        Grid.SetRow(footerGrid, 3);
        root.Children.Add(footerGrid);

        return root;
    }

    private Button CreateNavButton(string label, OnboardingSection section)
    {
        var button = new Button
        {
            Content = label,
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 9, 0),
            Tag = section,
        };
        StyleButton(button, primary: section == _section);
        button.Click += (_, _) => ShowSection(section);
        return button;
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

        _sectionComplete.IsEnabled = section != OnboardingSection.Overview;
        _sectionComplete.IsChecked = section switch
        {
            OnboardingSection.Jailbreak => _state.JailbreakGuideCompleted,
            OnboardingSection.Downgrade => _state.DowngradeGuideCompleted,
            OnboardingSection.ColdBoot => _state.ColdBootGuideCompleted,
            _ => false,
        };
        _sectionComplete.Content = section == OnboardingSection.Overview
            ? "Open each workflow guide before performing hardware operations"
            : $"I read and understand the {SectionName(section)} guide";
        _openWorkflowButton.Visibility = section == OnboardingSection.Overview
            ? Visibility.Collapsed
            : Visibility.Visible;
        _openWorkflowButton.Content = section == OnboardingSection.Jailbreak
            ? "Open Jailbreak tab"
            : "Open Downgrade tab";
    }

    private void RenderOverview()
    {
        _sectionTitle.Text = "Pick the workflow that matches your goal";
        _sectionSubtitle.Text = "Jailbreak and downgrade are different operations. Cold Boot is the repeatable boot procedure required after a tethered downgrade.";
        AddCallout("Only one hardware workflow runs at a time", "The app locks Apple USB ownership while Jailbreak, Downgrade, Recovery, or Cold Boot is active. Do not run separate gaster, Zadig, usbipd, or openra1n commands beside the app.", WarningBrush);
        AddWorkflowCard("Jailbreak", "Keep the currently installed firmware and boot palera1n. After a full reboot, run the Jailbreak workflow again to re-enable the jailbreak.", "Does not downgrade or erase firmware.");
        AddWorkflowCard("Downgrade", "Erase the device and install a supported iOS/iPadOS 15 build using the DarkSword restore path. The non-destructive DFU → PongoOS test must pass first.", "Creates a device-specific session and boot profile.");
        AddWorkflowCard("Cold Boot", "Boot a device that was already downgraded. Use the saved boot-profile.json after every shutdown, restart, or dead battery.", "Does not repeat the downgrade.");
    }

    private void RenderJailbreak()
    {
        _sectionTitle.Text = "Jailbreak onboarding";
        _sectionSubtitle.Text = "Use this path to jailbreak the firmware already installed on the device.";
        AddCallout("Before you start", "Run Palera1nWin as Administrator, complete Setup, keep WSL/Ubuntu available, use a reliable data cable, and close iTunes or Apple Devices if it is actively syncing.", AccentBrush);
        AddStep(1, "Choose the mode", "Rootless is recommended. Rootful changes the system volume and uses more storage. Safe Mode boots without tweak injection; Verbose Boot shows detailed boot output.");
        AddStep(2, "Press Start Jailbreak", "The app validates the shared toolchain, stops conflicting Apple services only for the transaction, and guides the device into DFU.");
        AddStep(3, "Follow the DFU prompts", "The screen must remain black in DFU. The app transfers USB ownership between Windows and WSL, verifies the DFU driver, runs openra1n, verifies PongoOS, and then runs palera1n.");
        AddStep(4, "After the device boots", "Install or open the palera1n loader/bootstrap shown on the device. A normal reboot removes the active jailbreak state; return to this tab and run Start Jailbreak again.");
        AddCallout("Do not use the Cold Boot button for a normal jailbreak", "Cold Boot is only for a DarkSword-downgraded device with a matching boot-profile.json. A stock-firmware palera1n reboot belongs in the Jailbreak tab.", DangerBrush);
    }

    private void RenderDowngrade()
    {
        _sectionTitle.Text = "Downgrade onboarding";
        _sectionSubtitle.Text = "This is a destructive, tethered restore. Read every stage before selecting Start Full Downgrade.";
        AddCallout("The downgrade erases the device", "Create an encrypted Apple Devices/iTunes backup, separately copy photos and files, save authenticator recovery codes, and know the Apple ID password used by Activation Lock.", DangerBrush);
        AddStep(1, "Connect and identify the exact device", "The app reads ProductType automatically. Never choose a model manually or use an IPSW for a different ProductType.");
        AddStep(2, "Select and inspect the iOS/iPadOS 15 IPSW", "The app verifies BuildManifest, target ProductType, version, file size, and hashes before it can be used.");
        AddStep(3, "Run Test DFU → PongoOS", "This is non-destructive. It proves the USB driver transaction, checkm8, PongoOS enumeration, and bridge access before any firmware is erased.");
        AddStep(4, "Complete preflight and confirmations", "Confirm the backup, tethered-boot requirement, ownership, Activation Lock preparation, and type the exact ProductType shown by the app.");
        AddStep(5, "Start Full Downgrade", "The app captures and validates SHC/PTE artifacts, restores firmware, creates the exact-device cold-boot profile, and performs the first tethered boot.");
        AddCallout("Keep the entire session folder", "The session folder contains boot-profile.json, the device-specific PTE file, metadata, hashes, and restore state. Back up the whole folder to another drive.", WarningBrush);
    }

    private void RenderColdBoot()
    {
        _sectionTitle.Text = "Cold Boot onboarding";
        _sectionSubtitle.Text = "Use this every time a DarkSword-downgraded device fully powers off, restarts, or loses its battery charge.";
        AddCallout("Required file: boot-profile.json", "Import boot-profile.json from the completed DarkSword session. The app automatically finds it for the same ECID when available. A raw PTE .bin by itself is intentionally blocked.", AccentBrush);
        AddStep(1, "Open Palera1nWin and connect the downgraded device", "The Downgrade tab shows the saved target version, build, session, ProductType, and ECID suffix when the profile is loaded.");
        AddStep(2, "Enter DFU mode", "Use the timed DFU guide. The device screen stays completely black. Recovery mode with a cable/computer image is not DFU.");
        AddStep(3, "Press Boot Device", "Before any payload is sent, the app rechecks ProductType, ECID, PTE hash, sep_racer.bin, and kpf.bin against the saved profile.");
        AddStep(4, "Wait for iOS/iPadOS 15 to start", "The app obtains PongoOS, loads the saved SEP/PTE/KPF boot plan, sends one final boot command, and waits for the device to return.");
        AddCallout("Back up the complete session folder", "Do not move only boot-profile.json and delete the referenced PTE. Keep both files and their metadata together. Use Open Session Folder from the Cold Boot card to locate them.", WarningBrush);
        AddCallout("When Boot Device is blocked", "Import the correct boot-profile.json, reconnect the exact device, enter DFU, and review the message. Never fix a mismatch by editing the JSON or selecting another device's PTE.", DangerBrush);
    }

    private void AddWorkflowCard(string title, string body, string footer)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = body,
            Foreground = MutedBrush,
            Margin = new Thickness(0, 6, 0, 8),
            TextWrapping = TextWrapping.Wrap,
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
        text.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 16,
            FontWeight = FontWeights.SemiBold,
        });
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
            Foreground = Brushes.White,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 20,
        });
        var card = CreateCard(panel);
        card.BorderBrush = accent;
        _sectionContent.Children.Add(card);
    }

    private static Border CreateCard(UIElement child) => new()
    {
        Background = PanelAltBrush,
        BorderBrush = StrokeBrush,
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(16),
        Margin = new Thickness(0, 0, 0, 12),
        Child = child,
    };

    private static void StyleButton(Button button, bool primary)
    {
        button.BorderThickness = new Thickness(1);
        button.BorderBrush = primary ? AccentBrush : StrokeBrush;
        button.Background = primary ? AccentBrush : PanelAltBrush;
        button.Foreground = primary ? Brushes.Black : Brushes.White;
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
