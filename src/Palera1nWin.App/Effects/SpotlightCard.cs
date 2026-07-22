using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace Palera1nWin.App.Effects;

public sealed class SpotlightCard : Border
{
    public static readonly DependencyProperty GlowColorProperty = DependencyProperty.Register(
        nameof(GlowColor),
        typeof(Color),
        typeof(SpotlightCard),
        new FrameworkPropertyMetadata(Color.FromRgb(0x4c, 0xc9, 0xf0), FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty GlowStrengthProperty = DependencyProperty.Register(
        nameof(GlowStrength),
        typeof(double),
        typeof(SpotlightCard),
        new FrameworkPropertyMetadata(0.95, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty SpotlightSizeProperty = DependencyProperty.Register(
        nameof(SpotlightSize),
        typeof(double),
        typeof(SpotlightCard),
        new FrameworkPropertyMetadata(220.0, FrameworkPropertyMetadataOptions.AffectsRender));

    private Point _pointer;
    private bool _hasPointer;

    public SpotlightCard()
    {
        Background = Brushes.Transparent;
        BorderBrush = Brushes.Transparent;
        BorderThickness = new Thickness(0);
        CornerRadius = new CornerRadius(10);
        Padding = new Thickness(18);
        ClipToBounds = false;
        SnapsToDevicePixels = true;

        MouseMove += OnMouseMove;
        MouseLeave += OnMouseLeave;
    }

    public Color GlowColor
    {
        get => (Color)GetValue(GlowColorProperty);
        set => SetValue(GlowColorProperty, value);
    }

    public double GlowStrength
    {
        get => (double)GetValue(GlowStrengthProperty);
        set => SetValue(GlowStrengthProperty, value);
    }

    public double SpotlightSize
    {
        get => (double)GetValue(SpotlightSizeProperty);
        set => SetValue(SpotlightSizeProperty, value);
    }

    protected override void OnRender(DrawingContext dc)
    {
        var rect = new Rect(0, 0, ActualWidth, ActualHeight);
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return;
        }

        var radius = Math.Max(CornerRadius.TopLeft, 8);
        var baseBrush = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(1, 1)
        };
        baseBrush.GradientStops.Add(new GradientStop(Color.FromArgb(218, 18, 27, 34), 0));
        baseBrush.GradientStops.Add(new GradientStop(Color.FromArgb(192, 15, 22, 29), 0.58));
        baseBrush.GradientStops.Add(new GradientStop(Color.FromArgb(178, 20, 28, 34), 1));
        baseBrush.Freeze();
        dc.DrawRoundedRectangle(baseBrush, null, rect, radius, radius);

        if (_hasPointer)
        {
            var relativeX = Math.Clamp(_pointer.X / Math.Max(rect.Width, 1), 0, 1);
            var relativeY = Math.Clamp(_pointer.Y / Math.Max(rect.Height, 1), 0, 1);
            var glow = new RadialGradientBrush
            {
                Center = new Point(relativeX, relativeY),
                GradientOrigin = new Point(relativeX, relativeY),
                RadiusX = SpotlightSize / Math.Max(rect.Width, 1),
                RadiusY = SpotlightSize / Math.Max(rect.Height, 1),
                Opacity = Math.Clamp(GlowStrength, 0, 1)
            };
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(88, GlowColor.R, GlowColor.G, GlowColor.B), 0));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(30, GlowColor.R, GlowColor.G, GlowColor.B), 0.42));
            glow.GradientStops.Add(new GradientStop(Color.FromArgb(0, GlowColor.R, GlowColor.G, GlowColor.B), 1));
            glow.Freeze();
            dc.DrawRoundedRectangle(glow, null, rect, radius, radius);
        }

        var borderColor = _hasPointer
            ? Color.FromArgb(185, GlowColor.R, GlowColor.G, GlowColor.B)
            : Color.FromArgb(120, 72, 89, 105);
        var borderPen = new Pen(new SolidColorBrush(borderColor), _hasPointer ? 1.35 : 1.0);
        borderPen.Freeze();
        dc.DrawRoundedRectangle(null, borderPen, new Rect(0.5, 0.5, Math.Max(0, rect.Width - 1), Math.Max(0, rect.Height - 1)), radius, radius);
    }

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        _pointer = e.GetPosition(this);
        _hasPointer = true;
        InvalidateVisual();
    }

    private void OnMouseLeave(object sender, MouseEventArgs e)
    {
        _hasPointer = false;
        InvalidateVisual();
    }
}
