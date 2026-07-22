using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class JailbreakView
{
    private TextBlock? _jailbreakOnboardingStatus;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(InjectJailbreakOnboarding));
    }

    private void InjectJailbreakOnboarding()
    {
        if (Content is not ScrollViewer scroll || scroll.Content is not StackPanel root) return;
        if (root.Children.OfType<FrameworkElement>().Any(element => Equals(element.Tag, "jailbreak-onboarding"))) return;

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock
        {
            Text = "Jailbreak onboarding",
            FontSize = 18,
            FontWeight = FontWeights.SemiBold,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "Use this workflow to jailbreak the firmware already installed on the device. It does not downgrade or erase the device.",
            Margin = new Thickness(0, 5, 0, 12),
            Foreground = FindBrush("Brush.TextSecondary", Brushes.LightGray),
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(CreateJailbreakStep("1", "Complete Setup and run the app as Administrator."));
        panel.Children.Add(CreateJailbreakStep("2", "Choose Rootless unless you specifically need Rootful; Safe Mode disables tweak injection."));
        panel.Children.Add(CreateJailbreakStep("3", "Press Start Jailbreak and follow the timed DFU instructions. The screen must remain black."));
        panel.Children.Add(CreateJailbreakStep("4", "After a full reboot, return here and run Start Jailbreak again to re-enable the jailbreak."));

        _jailbreakOnboardingStatus = new TextBlock
        {
            Margin = new Thickness(0, 10, 0, 0),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        UpdateJailbreakOnboardingStatus();
        panel.Children.Add(_jailbreakOnboardingStatus);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 14, 0, 0),
        };
        var openGuide = CreateInlineButton("Open Full Jailbreak Guide", true);
        openGuide.Click += (_, _) => OnboardingWindow.ShowFor(this, OnboardingSection.Jailbreak);
        var markRead = CreateInlineButton("Mark Guide Read", false);
        markRead.Margin = new Thickness(8, 0, 0, 0);
        markRead.Click += (_, _) =>
        {
            var state = OnboardingStateStore.Load();
            OnboardingStateStore.MarkSectionComplete(state, OnboardingSection.Jailbreak);
            UpdateJailbreakOnboardingStatus();
        };
        buttons.Children.Add(openGuide);
        buttons.Children.Add(markRead);
        panel.Children.Add(buttons);

        var card = new Border
        {
            Tag = "jailbreak-onboarding",
            Background = FindBrush("ControlFillColorSecondaryBrush", new SolidColorBrush(Color.FromRgb(35, 40, 48))),
            BorderBrush = FindBrush("Brush.Accent", new SolidColorBrush(Color.FromRgb(67, 210, 194))),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(16),
            Margin = new Thickness(0, 18, 0, 0),
            Child = panel,
        };

        root.Children.Insert(Math.Min(2, root.Children.Count), card);
    }

    private UIElement CreateJailbreakStep(string number, string text)
    {
        var row = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var badge = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = FindBrush("Brush.Accent", new SolidColorBrush(Color.FromRgb(67, 210, 194))),
            Child = new TextBlock
            {
                Text = number,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 11,
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

    private Button CreateInlineButton(string content, bool primary)
    {
        return new Button
        {
            Content = content,
            Padding = new Thickness(14, 7, 14, 7),
            Background = primary
                ? FindBrush("Brush.Accent", new SolidColorBrush(Color.FromRgb(67, 210, 194)))
                : FindBrush("ControlFillColorSecondaryBrush", new SolidColorBrush(Color.FromRgb(35, 40, 48))),
            Foreground = primary ? Brushes.Black : Brushes.White,
            BorderBrush = FindBrush("ControlStrokeColorDefaultBrush", Brushes.DimGray),
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
        };
    }

    private Brush FindBrush(string key, Brush fallback) => TryFindResource(key) as Brush ?? fallback;

    private void UpdateJailbreakOnboardingStatus()
    {
        if (_jailbreakOnboardingStatus is null) return;
        var complete = OnboardingStateStore.Load().JailbreakGuideCompleted;
        _jailbreakOnboardingStatus.Text = complete
            ? "Guide status: Read. You can reopen it at any time."
            : "Guide status: Not marked read. Review it before the first jailbreak attempt.";
        _jailbreakOnboardingStatus.Foreground = complete
            ? FindBrush("Brush.Success", Brushes.LightGreen)
            : FindBrush("Brush.Accent", Brushes.Aquamarine);
    }
}
