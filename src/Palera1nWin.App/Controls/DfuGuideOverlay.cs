using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Controls;

public sealed class DfuGuideOverlay : Grid
{
    private static readonly Brush BackdropBrush = new SolidColorBrush(Color.FromArgb(225, 4, 7, 12));
    private static readonly Brush CardBrush = new SolidColorBrush(Color.FromRgb(17, 22, 31));
    private static readonly Brush SurfaceBrush = new SolidColorBrush(Color.FromRgb(26, 33, 46));
    private static readonly Brush StrokeBrush = new SolidColorBrush(Color.FromRgb(58, 70, 92));
    private static readonly Brush TextBrush = new SolidColorBrush(Color.FromRgb(244, 247, 251));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(178, 188, 205));
    private static readonly Brush AccentBrush = new SolidColorBrush(Color.FromRgb(45, 212, 191));
    private static readonly Brush AccentSoftBrush = new SolidColorBrush(Color.FromArgb(75, 45, 212, 191));
    private static readonly Brush SuccessBrush = new SolidColorBrush(Color.FromRgb(52, 211, 153));
    private static readonly Brush DangerBrush = new SolidColorBrush(Color.FromRgb(248, 113, 113));

    private readonly Border _card;
    private readonly TextBlock _eyebrow;
    private readonly TextBlock _title;
    private readonly TextBlock _instruction;
    private readonly TextBlock _detail;
    private readonly TextBlock _countdown;
    private readonly TextBlock _countdownUnit;
    private readonly ProgressBar _progress;
    private readonly Border _screen;
    private readonly TextBlock _screenState;
    private readonly Border _powerButton;
    private readonly Border _homeButton;
    private readonly Border _volumeDownButton;
    private readonly TextBlock _powerLabel;
    private readonly TextBlock _secondButtonLabel;
    private readonly Border[] _stepCards;
    private readonly TextBlock[] _stepState;
    private readonly Button _cancelButton;

    public DfuGuideOverlay()
    {
        Visibility = Visibility.Collapsed;
        Panel.SetZIndex(this, 10000);
        Background = BackdropBrush;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _card = new Border
        {
            Width = 930,
            MaxWidth = 1040,
            Margin = new Thickness(34),
            Padding = new Thickness(24),
            CornerRadius = new CornerRadius(18),
            Background = CardBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = 36,
                ShadowDepth = 0,
                Opacity = 0.62,
                Color = Colors.Black,
            },
        };
        Children.Add(_card);

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        _card.Child = cardGrid;

        var header = new Grid { Margin = new Thickness(2, 0, 2, 20) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerCopy = new StackPanel();
        headerCopy.Children.Add(new TextBlock
        {
            Text = "GUIDED DEVICE MODE",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush,
            CharacterSpacing = 120,
        });
        headerCopy.Children.Add(new TextBlock
        {
            Text = "Enter DFU mode",
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            Margin = new Thickness(0, 5, 0, 0),
        });
        headerCopy.Children.Add(new TextBlock
        {
            Text = "Follow the highlighted hardware buttons and the countdown exactly. Detection ends the guide automatically.",
            FontSize = 12.5,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 20, 0),
        });
        header.Children.Add(headerCopy);

        _cancelButton = new Button
        {
            Content = "Cancel guide",
            MinWidth = 118,
            Padding = new Thickness(15, 8, 15, 8),
            Background = SurfaceBrush,
            Foreground = TextBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(_cancelButton, 1);
        header.Children.Add(_cancelButton);
        cardGrid.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(330) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        cardGrid.Children.Add(body);

        var deviceStage = new Border
        {
            Margin = new Thickness(0, 0, 22, 0),
            Padding = new Thickness(20),
            CornerRadius = new CornerRadius(14),
            Background = new SolidColorBrush(Color.FromRgb(11, 15, 22)),
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
        };
        body.Children.Add(deviceStage);

        var stageGrid = new Grid();
        deviceStage.Child = stageGrid;

        var bezel = new Border
        {
            Width = 238,
            Height = 432,
            CornerRadius = new CornerRadius(34),
            Background = new SolidColorBrush(Color.FromRgb(21, 24, 30)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(82, 89, 102)),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect
            {
                BlurRadius = 20,
                ShadowDepth = 8,
                Opacity = 0.5,
                Color = Colors.Black,
            },
        };
        stageGrid.Children.Add(bezel);

        var deviceGrid = new Grid { Margin = new Thickness(13) };
        deviceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(35) });
        deviceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        deviceGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(58) });
        bezel.Child = deviceGrid;

        var speaker = new Border
        {
            Width = 54,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = new SolidColorBrush(Color.FromRgb(70, 75, 85)),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        deviceGrid.Children.Add(speaker);

        _screen = new Border
        {
            GridRow = 1,
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Black,
            BorderBrush = new SolidColorBrush(Color.FromRgb(48, 54, 65)),
            BorderThickness = new Thickness(1),
        };
        Grid.SetRow(_screen, 1);
        deviceGrid.Children.Add(_screen);

        var screenStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(18),
        };
        screenStack.Children.Add(new TextBlock
        {
            Text = "●",
            FontSize = 24,
            Foreground = AccentBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _screenState = new TextBlock
        {
            Text = "DISPLAY MUST STAY BLACK",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = TextBrush,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        screenStack.Children.Add(_screenState);
        screenStack.Children.Add(new TextBlock
        {
            Text = "No Apple logo\nNo cable graphic",
            FontSize = 10.5,
            Foreground = MutedBrush,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 7, 0, 0),
        });
        _screen.Child = screenStack;

        _homeButton = new Border
        {
            Width = 38,
            Height = 38,
            CornerRadius = new CornerRadius(19),
            Background = new SolidColorBrush(Color.FromRgb(22, 25, 31)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(105, 112, 126)),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetRow(_homeButton, 2);
        deviceGrid.Children.Add(_homeButton);

        _powerButton = HardwareButton(9, 74, HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 53, -10, 0));
        stageGrid.Children.Add(_powerButton);
        _volumeDownButton = HardwareButton(9, 56, HorizontalAlignment.Left, VerticalAlignment.Top, new Thickness(-10, 152, 0, 0));
        stageGrid.Children.Add(_volumeDownButton);

        _powerLabel = ButtonCallout("TOP / SIDE", HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 18, -2, 0));
        stageGrid.Children.Add(_powerLabel);
        _secondButtonLabel = ButtonCallout("HOME", HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(-2, 0, 0, 18));
        stageGrid.Children.Add(_secondButtonLabel);

        var instructionPanel = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(instructionPanel, 1);
        body.Children.Add(instructionPanel);

        _eyebrow = new TextBlock
        {
            Text = "GET READY",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = AccentBrush,
            CharacterSpacing = 110,
        };
        instructionPanel.Children.Add(_eyebrow);

        _title = new TextBlock
        {
            Text = "Keep the device connected",
            FontSize = 25,
            FontWeight = FontWeights.SemiBold,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 7, 0, 0),
        };
        instructionPanel.Children.Add(_title);

        _instruction = new TextBlock
        {
            FontSize = 14,
            LineHeight = 21,
            Foreground = TextBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 10, 0, 0),
        };
        instructionPanel.Children.Add(_instruction);

        var countdownRow = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        countdownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        countdownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var countdownRing = new Border
        {
            Width = 112,
            Height = 112,
            CornerRadius = new CornerRadius(56),
            BorderBrush = AccentBrush,
            BorderThickness = new Thickness(5),
            Background = AccentSoftBrush,
        };
        var countdownStack = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        _countdown = new TextBlock
        {
            Text = "3",
            FontSize = 46,
            FontWeight = FontWeights.Bold,
            Foreground = TextBrush,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        _countdownUnit = new TextBlock
        {
            Text = "SECONDS",
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            Foreground = MutedBrush,
            CharacterSpacing = 90,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, -4, 0, 0),
        };
        countdownStack.Children.Add(_countdown);
        countdownStack.Children.Add(_countdownUnit);
        countdownRing.Child = countdownStack;
        countdownRow.Children.Add(countdownRing);

        var countdownCopy = new StackPanel
        {
            Margin = new Thickness(18, 3, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _detail = new TextBlock
        {
            FontSize = 12,
            Foreground = MutedBrush,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 18,
        };
        countdownCopy.Children.Add(_detail);
        countdownCopy.Children.Add(new TextBlock
        {
            Text = "The guide stops as soon as real DFU is detected.",
            FontSize = 11,
            Foreground = AccentBrush,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Grid.SetColumn(countdownCopy, 1);
        countdownRow.Children.Add(countdownCopy);
        instructionPanel.Children.Add(countdownRow);

        _progress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Height = 8,
            Margin = new Thickness(0, 20, 0, 0),
            Foreground = AccentBrush,
            Background = SurfaceBrush,
        };
        instructionPanel.Children.Add(_progress);

        var warning = new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 14, 0, 0),
            CornerRadius = new CornerRadius(7),
            Background = new SolidColorBrush(Color.FromArgb(42, 251, 191, 36)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(110, 251, 191, 36)),
            BorderThickness = new Thickness(1),
        };
        warning.Child = new TextBlock
        {
            Text = "Keep the cable connected directly to the PC. A recovery cable/computer screen means the timing was missed.",
            Foreground = TextBrush,
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
        };
        instructionPanel.Children.Add(warning);

        var footer = new Grid { Margin = new Thickness(0, 20, 0, 0) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _stepCards = new Border[3];
        _stepState = new TextBlock[3];
        _stepCards[0] = BuildStepCard(0, "1", "Prepare", "Hands on buttons");
        _stepCards[1] = BuildStepCard(1, "2", "Hold both", "8-second hold");
        _stepCards[2] = BuildStepCard(2, "3", "Release Power", "Keep second button held");
        foreach (var card in _stepCards) footer.Children.Add(card);
        Grid.SetRow(footer, 2);
        cardGrid.Children.Add(footer);
    }

    public event EventHandler? CancelRequested;

    public void Open(DfuGuideButtonProfile profile)
    {
        Visibility = Visibility.Visible;
        _secondButtonLabel.Text = profile == DfuGuideButtonProfile.VolumeDown ? "VOLUME DOWN" : "HOME";
        _volumeDownButton.Visibility = profile == DfuGuideButtonProfile.VolumeDown ? Visibility.Visible : Visibility.Collapsed;
        _homeButton.Visibility = profile == DfuGuideButtonProfile.Home ? Visibility.Visible : Visibility.Collapsed;
        _cancelButton.IsEnabled = true;
        Apply(new DfuGuideFrame(
            DfuGuidePhase.Preparing,
            profile,
            "GET READY",
            "Keep the device connected",
            "Place your fingers on the highlighted buttons. The sequence begins immediately.",
            "Follow every countdown exactly.",
            3,
            0,
            false,
            false,
            false));
    }

    public void Close()
    {
        StopPulse(_powerButton);
        StopPulse(_homeButton);
        StopPulse(_volumeDownButton);
        Visibility = Visibility.Collapsed;
    }

    public void Apply(DfuGuideFrame frame)
    {
        if (frame.Phase == DfuGuidePhase.Hidden)
        {
            Close();
            return;
        }

        Visibility = Visibility.Visible;
        _eyebrow.Text = frame.Eyebrow;
        _title.Text = frame.Title;
        _instruction.Text = frame.Instruction;
        _detail.Text = frame.Detail;
        _progress.Value = Math.Clamp(frame.Progress, 0, 100);
        _countdown.Text = frame.SecondsRemaining?.ToString() ?? (frame.Phase == DfuGuidePhase.Detected ? "✓" : "—");
        _countdownUnit.Text = frame.SecondsRemaining is null ? "STATUS" : "SECONDS";

        _secondButtonLabel.Text = frame.Profile == DfuGuideButtonProfile.VolumeDown ? "VOLUME DOWN" : "HOME";
        _volumeDownButton.Visibility = frame.Profile == DfuGuideButtonProfile.VolumeDown ? Visibility.Visible : Visibility.Collapsed;
        _homeButton.Visibility = frame.Profile == DfuGuideButtonProfile.Home ? Visibility.Visible : Visibility.Collapsed;

        SetActive(_powerButton, frame.PowerButtonActive);
        SetActive(_homeButton, frame.HomeButtonActive);
        SetActive(_volumeDownButton, frame.VolumeDownButtonActive);

        var accent = frame.Phase switch
        {
            DfuGuidePhase.Detected => SuccessBrush,
            DfuGuidePhase.Failed => DangerBrush,
            _ => AccentBrush,
        };
        _eyebrow.Foreground = accent;
        _progress.Foreground = accent;
        _screen.BorderBrush = frame.Phase == DfuGuidePhase.Detected ? SuccessBrush : StrokeBrush;
        _screenState.Text = frame.Phase == DfuGuidePhase.Detected
            ? "DFU DETECTED — DISPLAY BLACK"
            : "DISPLAY MUST STAY BLACK";
        _screenState.Foreground = frame.Phase == DfuGuidePhase.Detected ? SuccessBrush : TextBrush;

        var activeStep = frame.Phase switch
        {
            DfuGuidePhase.Preparing => 0,
            DfuGuidePhase.HoldBoth => 1,
            DfuGuidePhase.HoldSecond or DfuGuidePhase.WaitingForDevice => 2,
            DfuGuidePhase.Detected => 3,
            _ => -1,
        };
        for (var index = 0; index < _stepCards.Length; index++)
        {
            var complete = activeStep > index;
            var active = activeStep == index;
            _stepCards[index].BorderBrush = complete ? SuccessBrush : active ? AccentBrush : StrokeBrush;
            _stepCards[index].Background = complete
                ? new SolidColorBrush(Color.FromArgb(40, 52, 211, 153))
                : active ? AccentSoftBrush : SurfaceBrush;
            _stepState[index].Text = complete ? "DONE" : active ? "NOW" : "NEXT";
            _stepState[index].Foreground = complete ? SuccessBrush : active ? AccentBrush : MutedBrush;
        }
    }

    private Border BuildStepCard(int column, string number, string title, string detail)
    {
        var card = new Border
        {
            Margin = new Thickness(column == 0 ? 0 : 6, 0, column == 2 ? 0 : 6, 0),
            Padding = new Thickness(12),
            CornerRadius = new CornerRadius(9),
            Background = SurfaceBrush,
            BorderBrush = StrokeBrush,
            BorderThickness = new Thickness(1),
        };
        Grid.SetColumn(card, column);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(30) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var badge = new Border
        {
            Width = 24,
            Height = 24,
            CornerRadius = new CornerRadius(12),
            Background = AccentBrush,
            Child = new TextBlock
            {
                Text = number,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        grid.Children.Add(badge);
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock { Text = title, Foreground = TextBrush, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        copy.Children.Add(new TextBlock { Text = detail, Foreground = MutedBrush, FontSize = 10.5, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(copy, 1);
        grid.Children.Add(copy);
        _stepState[column] = new TextBlock
        {
            Text = "NEXT",
            Foreground = MutedBrush,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(_stepState[column], 2);
        grid.Children.Add(_stepState[column]);
        card.Child = grid;
        return card;
    }

    private static Border HardwareButton(
        double width,
        double height,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        Thickness margin) => new()
        {
            Width = width,
            Height = height,
            CornerRadius = new CornerRadius(4),
            Background = new SolidColorBrush(Color.FromRgb(85, 92, 105)),
            BorderBrush = new SolidColorBrush(Color.FromRgb(122, 130, 145)),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = margin,
        };

    private static TextBlock ButtonCallout(
        string text,
        HorizontalAlignment horizontal,
        VerticalAlignment vertical,
        Thickness margin) => new()
        {
            Text = text,
            Foreground = MutedBrush,
            FontSize = 9.5,
            FontWeight = FontWeights.Bold,
            HorizontalAlignment = horizontal,
            VerticalAlignment = vertical,
            Margin = margin,
        };

    private static void SetActive(Border button, bool active)
    {
        StopPulse(button);
        button.BorderBrush = active ? AccentBrush : new SolidColorBrush(Color.FromRgb(122, 130, 145));
        button.Background = active ? AccentBrush : new SolidColorBrush(Color.FromRgb(85, 92, 105));
        if (!active) return;

        var pulse = new DoubleAnimation
        {
            From = 0.52,
            To = 1,
            Duration = TimeSpan.FromMilliseconds(520),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        button.BeginAnimation(OpacityProperty, pulse, HandoffBehavior.SnapshotAndReplace);
        button.Effect = new DropShadowEffect
        {
            BlurRadius = 18,
            ShadowDepth = 0,
            Opacity = 0.9,
            Color = Color.FromRgb(45, 212, 191),
        };
    }

    private static void StopPulse(Border button)
    {
        button.BeginAnimation(OpacityProperty, null);
        button.Opacity = 1;
        button.Effect = null;
    }
}
