using System.Windows;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private void DeferredDowngradeExperience_Loaded(object sender, RoutedEventArgs e)
    {
        Loaded -= DeferredDowngradeExperience_Loaded;
        WireDowngradeExperienceHooks();
        Experience_Loaded(sender, e);
    }
}
