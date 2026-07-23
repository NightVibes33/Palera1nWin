using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Controls;

public sealed class DfuGuideOverlay : Grid
{
    private static readonly Brush Backdrop = Brush(225, 4, 7, 12);
    private static readonly Brush Card = Brush(255, 17, 22, 31);
    private static readonly Brush Surface = Brush(255, 26, 33, 46);
    private static readonly Brush Stroke = Brush(255, 58, 70, 92);
    private static readonly Brush Text = Brush(255, 244, 247, 251);
    private static readonly Brush Muted = Brush(255, 178, 188, 205);
    private static readonly Brush Accent = Brush(255, 45, 212, 191);
    private static readonly Brush AccentSoft = Brush(75, 45, 212, 191);
    private static readonly Brush Success = Brush(255, 52, 211, 153);
    private static readonly Brush Danger = Brush(255, 248, 113, 113);

    private readonly TextBlock _eyebrow = Label(11, FontWeights.Bold, Accent);
    private readonly TextBlock _title = Label(25, FontWeights.SemiBold, Text);
    private readonly TextBlock _instruction = Label(14, FontWeights.Normal, Text);
    private readonly TextBlock _detail = Label(12, FontWeights.Normal, Muted);
    private readonly TextBlock _countdown = Label(46, FontWeights.Bold, Text);
    private readonly TextBlock _countdownUnit = Label(9.5, FontWeights.Bold, Muted);
    private readonly TextBlock _screenState = Label(10.5, FontWeights.Bold, Text);
    private readonly TextBlock _secondButtonLabel = Label(9.5, FontWeights.Bold, Muted);
    private readonly ProgressBar _progress = new() { Minimum = 0, Maximum = 100, Height = 8 };
    private readonly Border _screen;
    private readonly Border _powerButton;
    private readonly Border _homeButton;
    private readonly Border _volumeDownButton;
    private readonly Border[] _stepCards = new Border[3];
    private readonly TextBlock[] _stepStates = new TextBlock[3];

    public DfuGuideOverlay()
    {
        Visibility = Visibility.Collapsed;
        Panel.SetZIndex(this, 10000);
        Background = Backdrop;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        var modal = new Border
        {
            Width = 850,
            MaxHeight = 650,
            Margin = new Thickness(24),
            Padding = new Thickness(22),
            CornerRadius = new CornerRadius(18),
            Background = Card,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 36, ShadowDepth = 0, Opacity = 0.62, Color = Colors.Black },
        };
        Children.Add(modal);

        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        modal.Child = layout;

        var header = new Grid { Margin = new Thickness(2, 0, 2, 18) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headerCopy = new StackPanel();
        headerCopy.Children.Add(new TextBlock
        {
            Text = "GUIDED DEVICE MODE",
            FontSize = 11,
            FontWeight = FontWeights.Bold,
            Foreground = Accent,
        });
        headerCopy.Children.Add(new TextBlock
        {
            Text = "Enter DFU mode",
            FontSize = 27,
            FontWeight = FontWeights.SemiBold,
            Foreground = Text,
            Margin = new Thickness(0, 5, 0, 0),
        });
        headerCopy.Children.Add(new TextBlock
        {
            Text = "Follow the highlighted buttons and countdown exactly. Real DFU detection ends the guide automatically.",
            FontSize = 12.5,
            Foreground = Muted,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 5, 20, 0),
        });
        header.Children.Add(headerCopy);

        var cancel = new Button
        {
            Content = "Cancel guide",
            MinWidth = 116,
            Padding = new Thickness(15, 8, 15, 8),
            Background = Surface,
            Foreground = Text,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
            FontWeight = FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        };
        cancel.Click += (_, _) => CancelRequested?.Invoke(this, EventArgs.Empty);
        Grid.SetColumn(cancel, 1);
        header.Children.Add(cancel);
        layout.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(290) });
        body.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(body, 1);
        layout.Children.Add(body);

        var stage = new Border
        {
            Margin = new Thickness(0, 0, 20, 0),
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(14),
            Background = Brush(255, 11, 15, 22),
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
        };
        body.Children.Add(stage);
        var stageGrid = new Grid();
        stage.Child = stageGrid;

        var bezel = new Border
        {
            Width = 215,
            Height = 390,
            CornerRadius = new CornerRadius(31),
            Background = Brush(255, 21, 24, 30),
            BorderBrush = Brush(255, 82, 89, 102),
            BorderThickness = new Thickness(2),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 8, Opacity = 0.5, Color = Colors.Black },
        };
        stageGrid.Children.Add(bezel);

        var device = new Grid { Margin = new Thickness(12) };
        device.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
        device.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        device.RowDefinitions.Add(new RowDefinition { Height = new GridLength(52) });
        bezel.Child = device;
        device.Children.Add(new Border
        {
            Width = 50,
            Height = 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush(255, 70, 75, 85),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });

        _screen = new Border
        {
            CornerRadius = new CornerRadius(6),
            Background = Brushes.Black,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
        };
        Grid.SetRow(_screen, 1);
        device.Children.Add(_screen);
        var screenCopy = new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(16),
        };
        screenCopy.Children.Add(new TextBlock
        {
            Text = "●",
            FontSize = 24,
            Foreground = Accent,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        _screenState.Text = "DISPLAY MUST STAY BLACK";
        _screenState.TextAlignment = TextAlignment.Center;
        _screenState.TextWrapping = TextWrapping.Wrap;
        _screenState.Margin = new Thickness(0, 9, 0, 0);
        screenCopy.Children.Add(_screenState);
        screenCopy.Children.Add(new TextBlock
        {
            Text = "No Apple logo\nNo cable graphic",
            FontSize = 10,
            Foreground = Muted,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 7, 0, 0),
        });
        _screen.Child = screenCopy;

        _homeButton = HardwareButton(36, 36, 18);
        _homeButton.HorizontalAlignment = HorizontalAlignment.Center;
        _homeButton.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetRow(_homeButton, 2);
        device.Children.Add(_homeButton);

        _powerButton = HardwareButton(9, 67, 4);
        _powerButton.HorizontalAlignment = HorizontalAlignment.Right;
        _powerButton.VerticalAlignment = VerticalAlignment.Top;
        _powerButton.Margin = new Thickness(0, 48, -9, 0);
        stageGrid.Children.Add(_powerButton);

        _volumeDownButton = HardwareButton(9, 53, 4);
        _volumeDownButton.HorizontalAlignment = HorizontalAlignment.Left;
        _volumeDownButton.VerticalAlignment = VerticalAlignment.Top;
        _volumeDownButton.Margin = new Thickness(-9, 140, 0, 0);
        stageGrid.Children.Add(_volumeDownButton);

        stageGrid.Children.Add(Callout("TOP / SIDE", HorizontalAlignment.Right, VerticalAlignment.Top, new Thickness(0, 15, 0, 0)));
        _secondButtonLabel.Text = "HOME";
        _secondButtonLabel.HorizontalAlignment = HorizontalAlignment.Left;
        _secondButtonLabel.VerticalAlignment = VerticalAlignment.Bottom;
        _secondButtonLabel.Margin = new Thickness(0, 0, 0, 15);
        stageGrid.Children.Add(_secondButtonLabel);

        var instructions = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(instructions, 1);
        body.Children.Add(instructions);
        _eyebrow.Text = "GET READY";
        instructions.Children.Add(_eyebrow);
        _title.TextWrapping = TextWrapping.Wrap;
        _title.Margin = new Thickness(0, 7, 0, 0);
        instructions.Children.Add(_title);
        _instruction.TextWrapping = TextWrapping.Wrap;
        _instruction.LineHeight = 21;
        _instruction.Margin = new Thickness(0, 10, 0, 0);
        instructions.Children.Add(_instruction);

        var countdownRow = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        countdownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        countdownRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var ring = new Border
        {
            Width = 104,
            Height = 104,
            CornerRadius = new CornerRadius(52),
            BorderBrush = Accent,
            BorderThickness = new Thickness(5),
            Background = AccentSoft,
        };
        var timerCopy = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        _countdown.Text = "3";
        _countdown.HorizontalAlignment = HorizontalAlignment.Center;
        _countdownUnit.Text = "SECONDS";
        _countdownUnit.HorizontalAlignment = HorizontalAlignment.Center;
        _countdownUnit.Margin = new Thickness(0, -4, 0, 0);
        timerCopy.Children.Add(_countdown);
        timerCopy.Children.Add(_countdownUnit);
        ring.Child = timerCopy;
        countdownRow.Children.Add(ring);

        var countDetail = new StackPanel { Margin = new Thickness(17, 3, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        _detail.TextWrapping = TextWrapping.Wrap;
        _detail.LineHeight = 18;
        countDetail.Children.Add(_detail);
        countDetail.Children.Add(new TextBlock
        {
            Text = "The countdown stops the moment Windows sees DFU.",
            FontSize = 11,
            Foreground = Accent,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
        Grid.SetColumn(countDetail, 1);
        countdownRow.Children.Add(countDetail);
        instructions.Children.Add(countdownRow);

        _progress.Margin = new Thickness(0, 18, 0, 0);
        _progress.Foreground = Accent;
        _progress.Background = Surface;
        instructions.Children.Add(_progress);
        instructions.Children.Add(new Border
        {
            Padding = new Thickness(12, 9, 12, 9),
            Margin = new Thickness(0, 13, 0, 0),
            CornerRadius = new CornerRadius(7),
            Background = Brush(42, 251, 191, 36),
            BorderBrush = Brush(110, 251, 191, 36),
            BorderThickness = new Thickness(1),
            Child = new TextBlock
            {
                Text = "Keep the cable connected directly to the PC. A recovery cable/computer screen means the timing was missed.",
                Foreground = Text,
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
            },
        });

        var footer = new Grid { Margin = new Thickness(0, 18, 0, 0) };
        for (var index = 0; index < 3; index++) footer.ColumnDefinitions.Add(new ColumnDefinition());
        _stepCards[0] = BuildStepCard(0, "1", "Prepare", "Hands on buttons");
        _stepCards[1] = BuildStepCard(1, "2", "Hold both", "8-second hold");
        _stepCards[2] = BuildStepCard(2, "3", "Release Power", "Keep second held");
        foreach (var step in _stepCards) footer.Children.Add(step);
        Grid.SetRow(footer, 2);
        layout.Children.Add(footer);
    }

    public event EventHandler? CancelRequested;

    public void Open(DfuGuideButtonProfile profile)
    {
        Visibility = Visibility.Visible;
        Apply(new DfuGuideFrame(
            DfuGuidePhase.Preparing,
            profile,
            "GET READY",
            "Keep the device connected",
            "Place your fingers on the highlighted buttons. The timed sequence begins immediately.",
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

        var volumeProfile = frame.Profile == DfuGuideButtonProfile.VolumeDown;
        _secondButtonLabel.Text = volumeProfile ? "VOLUME DOWN" : "HOME";
        _volumeDownButton.Visibility = volumeProfile ? Visibility.Visible : Visibility.Collapsed;
        _homeButton.Visibility = volumeProfile ? Visibility.Collapsed : Visibility.Visible;
        SetActive(_powerButton, frame.PowerButtonActive);
        SetActive(_homeButton, frame.HomeButtonActive);
        SetActive(_volumeDownButton, frame.VolumeDownButtonActive);

        var stateAccent = frame.Phase switch
        {
            DfuGuidePhase.Detected => Success,
            DfuGuidePhase.Failed => Danger,
            _ => Accent,
        };
        _eyebrow.Foreground = stateAccent;
        _progress.Foreground = stateAccent;
        _screen.BorderBrush = frame.Phase == DfuGuidePhase.Detected ? Success : Stroke;
        _screenState.Text = frame.Phase == DfuGuidePhase.Detected
            ? "DFU DETECTED — DISPLAY BLACK"
            : "DISPLAY MUST STAY BLACK";
        _screenState.Foreground = frame.Phase == DfuGuidePhase.Detected ? Success : Text;

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
            _stepCards[index].BorderBrush = complete ? Success : active ? Accent : Stroke;
            _stepCards[index].Background = complete ? Brush(40, 52, 211, 153) : active ? AccentSoft : Surface;
            _stepStates[index].Text = complete ? "DONE" : active ? "NOW" : "NEXT";
            _stepStates[index].Foreground = complete ? Success : active ? Accent : Muted;
        }
    }

    private Border BuildStepCard(int column, string number, string title, string detail)
    {
        var card = new Border
        {
            Margin = new Thickness(column == 0 ? 0 : 5, 0, column == 2 ? 0 : 5, 0),
            Padding = new Thickness(11),
            CornerRadius = new CornerRadius(9),
            Background = Surface,
            BorderBrush = Stroke,
            BorderThickness = new Thickness(1),
        };
        Grid.SetColumn(card, column);
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(new Border
        {
            Width = 23,
            Height = 23,
            CornerRadius = new CornerRadius(12),
            Background = Accent,
            Child = new TextBlock
            {
                Text = number,
                Foreground = Brushes.Black,
                FontWeight = FontWeights.Bold,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        });
        var copy = new StackPanel();
        copy.Children.Add(new TextBlock { Text = title, Foreground = Text, FontWeight = FontWeights.SemiBold, FontSize = 11.5 });
        copy.Children.Add(new TextBlock { Text = detail, Foreground = Muted, FontSize = 10, Margin = new Thickness(0, 3, 0, 0) });
        Grid.SetColumn(copy, 1);
        row.Children.Add(copy);
        _stepStates[column] = new TextBlock
        {
            Text = "NEXT",
            Foreground = Muted,
            FontSize = 9,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Top,
        };
        Grid.SetColumn(_stepStates[column], 2);
        row.Children.Add(_stepStates[column]);
        card.Child = row;
        return card;
    }

    private static Border HardwareButton(double width, double height, double radius) => new()
    {
        Width = width,
        Height = height,
        CornerRadius = new CornerRadius(radius),
        Background = Brush(255, 85, 92, 105),
        BorderBrush = Brush(255, 122, 130, 145),
        BorderThickness = new Thickness(1),
    };

    private static TextBlock Callout(string value, HorizontalAlignment horizontal, VerticalAlignment vertical, Thickness margin) => new()
    {
        Text = value,
        Foreground = Muted,
        FontSize = 9.5,
        FontWeight = FontWeights.Bold,
        HorizontalAlignment = horizontal,
        VerticalAlignment = vertical,
        Margin = margin,
    };

    private static TextBlock Label(double size, FontWeight weight, Brush foreground) => new()
    {
        FontSize = size,
        FontWeight = weight,
        Foreground = foreground,
    };

    private static SolidColorBrush Brush(byte alpha, byte red, byte green, byte blue) =>
        new(Color.FromArgb(alpha, red, green, blue));

    private static void SetActive(Border button, bool active)
    {
        StopPulse(button);
        button.BorderBrush = active ? Accent : Brush(255, 122, 130, 145);
        button.Background = active ? Accent : Brush(255, 85, 92, 105);
        if (!active) return;

        button.BeginAnimation(
            OpacityProperty,
            new DoubleAnimation
            {
                From = 0.52,
                To = 1,
                Duration = TimeSpan.FromMilliseconds(520),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
            },
            HandoffBehavior.SnapshotAndReplace);
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
