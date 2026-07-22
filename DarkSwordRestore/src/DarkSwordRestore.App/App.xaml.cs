using System.Windows;
using System.Windows.Threading;

namespace DarkSwordRestore.App;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += HandleDispatcherUnhandledException;
    }

    private static void HandleDispatcherUnhandledException(
        object sender,
        DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(
            e.Exception.Message,
            "DarkSword Restore",
            MessageBoxButton.OK,
            MessageBoxImage.Error);
        e.Handled = true;
    }
}
