using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Palera1nWin.App.Services;

internal static class ProgrammaticTheme
{
    public static Brush Brush(FrameworkElement owner, string key, Brush fallback)
    {
        return owner.TryFindResource(key) as Brush
            ?? Application.Current?.TryFindResource(key) as Brush
            ?? fallback;
    }

    public static void ApplyCard(FrameworkElement owner, Border card, bool secondary = false)
    {
        card.Background = Brush(
            owner,
            secondary ? "Brush.SurfaceSecondary" : "Brush.Card",
            new SolidColorBrush(secondary ? Color.FromRgb(27, 33, 48) : Color.FromRgb(21, 26, 36)));
        card.BorderBrush = Brush(owner, "Brush.Border", new SolidColorBrush(Color.FromRgb(53, 64, 87)));
        TextElement.SetForeground(card, Brush(owner, "Brush.Text", Brushes.White));
        ApplyTextContrast(owner, card);
    }

    public static void ApplyTextContrast(FrameworkElement owner, DependencyObject root)
    {
        var primary = Brush(owner, "Brush.Text", Brushes.White);
        ApplyTextContrastRecursive(root, primary);
    }

    private static void ApplyTextContrastRecursive(DependencyObject current, Brush primary)
    {
        switch (current)
        {
            case TextBlock text when text.ReadLocalValue(TextBlock.ForegroundProperty) == DependencyProperty.UnsetValue:
                text.Foreground = primary;
                break;
            case CheckBox checkBox when checkBox.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue:
                checkBox.Foreground = primary;
                break;
            case RadioButton radioButton when radioButton.ReadLocalValue(Control.ForegroundProperty) == DependencyProperty.UnsetValue:
                radioButton.Foreground = primary;
                break;
        }

        switch (current)
        {
            case Panel panel:
                foreach (UIElement child in panel.Children)
                {
                    ApplyTextContrastRecursive(child, primary);
                }
                break;
            case Decorator decorator when decorator.Child is not null:
                ApplyTextContrastRecursive(decorator.Child, primary);
                break;
            case ContentControl contentControl when contentControl.Content is DependencyObject content:
                ApplyTextContrastRecursive(content, primary);
                break;
            case ItemsControl itemsControl:
                foreach (var item in itemsControl.Items)
                {
                    if (item is DependencyObject dependencyObject)
                    {
                        ApplyTextContrastRecursive(dependencyObject, primary);
                    }
                }
                break;
        }
    }
}