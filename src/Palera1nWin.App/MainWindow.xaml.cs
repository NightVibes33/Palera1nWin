using System.Windows;
using System.Windows.Media;
using Palera1nWin.App.Services;
using Palera1nWin.App.ViewModels;
using Palera1nWin.App.Views;
using Wpf.Ui.Appearance;
using Wpf.Ui.Controls;
using Wpf.Ui.Interop;

namespace Palera1nWin.App;

public partial class MainWindow : FluentWindow
{
    private static readonly Type[] TabPages =
    [
        typeof(JailbreakView),
        typeof(DowngradeView),
        typeof(DeviceView),
        typeof(VersionsView),
        typeof(SetupView),
        typeof(LogsView),
        typeof(SettingsView),
        typeof(AboutView),
    ];

    private readonly PageService _pageService;
    private bool _firstRunOnboardingScheduled;

    public MainWindow(MainViewModel viewModel, int? initialTab = null)
    {
        InitializeComponent();
        DataContext = viewModel;

        FitToWorkArea();

        _pageService = new PageService(viewModel);
        RootNavigation.SetPageProviderService(_pageService);
        viewModel.NavigateRequested += NavigateToTab;

        Loaded += (_, _) =>
        {
            int tab = initialTab is int t && t >= 0 && t < TabPages.Length ? t : 0;
            NavigateToTab(tab);

            if (!_firstRunOnboardingScheduled)
            {
                _firstRunOnboardingScheduled = true;
                Dispatcher.BeginInvoke(
                    System.Windows.Threading.DispatcherPriority.ContextIdle,
                    new Action(() => OnboardingWindow.ShowFirstRun(this)));
            }
        };

        Closed += (_, _) =>
        {
            viewModel.NavigateRequested -= NavigateToTab;
            _pageService.Dispose();
        };
    }

    private void FitToWorkArea()
    {
        Rect work = SystemParameters.WorkArea;
        if (work.Width <= 0 || work.Height <= 0)
        {
            return;
        }

        double maxWidth = work.Width * 0.96;
        double maxHeight = work.Height * 0.96;

        Width = Math.Max(MinWidth, Math.Min(Width, maxWidth));
        Height = Math.Max(MinHeight, Math.Min(Height, maxHeight));
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        bool wantAcrylic = IsSystemTransparencyEnabled();
        ApplicationThemeManager.Apply(
            ApplicationTheme.Dark,
            wantAcrylic ? WindowBackdropType.Acrylic : WindowBackdropType.None,
            updateAccent: false);

        if (wantAcrylic && WindowBackdrop.ApplyBackdrop(this, WindowBackdropType.Acrylic))
        {
            WindowBackdropType = WindowBackdropType.Acrylic;
            Background = Brushes.Transparent;
            SmokeTint.Visibility = Visibility.Visible;
        }
        else if (TryFindResource("ApplicationBackgroundBrush") is Brush solid)
        {
            Background = solid;
        }
    }

    private static bool IsSystemTransparencyEnabled()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("EnableTransparency") is not int enabled || enabled != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    private void NavigateToTab(int tab)
    {
        if (tab >= 0 && tab < TabPages.Length)
        {
            RootNavigation.Navigate(TabPages[tab]);
        }
    }
}
