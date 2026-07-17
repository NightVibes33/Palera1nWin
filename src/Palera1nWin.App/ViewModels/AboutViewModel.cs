using System.Reflection;
using Palera1nWin.App.Mvvm;

namespace Palera1nWin.App.ViewModels;

public sealed class AboutViewModel : ObservableObject
{
    public AboutViewModel()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText = version is null ? "1.0.0" : version.ToString(3);

        OpenPalera1nSiteCommand = new RelayCommand(() => OpenUrl("https://palera.in/"));
        OpenGitHubCommand = new RelayCommand(() => OpenUrl("https://github.com/pwnapplehat/Palera1nWin"));
        OpenPalera1nRepoCommand = new RelayCommand(() => OpenUrl("https://github.com/palera1n/palera1n"));
    }

    public string VersionText { get; }

    public string LicenseText =>
        "Palera1nWin is open source under the MIT License.\n\n"
        + "Credits: the palera1n team and contributors who built the underlying jailbreak.";

    public RelayCommand OpenPalera1nSiteCommand { get; }

    public RelayCommand OpenGitHubCommand { get; }

    public RelayCommand OpenPalera1nRepoCommand { get; }

    private static void OpenUrl(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"Failed to open link: {ex.Message}",
                "Palera1nWin",
                System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
        }
    }
}
