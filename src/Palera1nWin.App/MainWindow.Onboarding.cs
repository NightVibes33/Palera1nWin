using System.Windows.Threading;
using Palera1nWin.App.Views;

namespace Palera1nWin.App;

public partial class MainWindow
{
    private bool _onboardingShown;

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        if (_onboardingShown) return;
        _onboardingShown = true;

        Dispatcher.BeginInvoke(
            DispatcherPriority.ApplicationIdle,
            new Action(() => OnboardingWindow.ShowFirstRun(this)));
    }

    public void NavigateToWorkflow(int tabIndex) => NavigateToTab(tabIndex);
}
