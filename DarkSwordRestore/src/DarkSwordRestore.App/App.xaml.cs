using System.Windows;

namespace DarkSwordRestore.App;

public partial class App : Application
{
    protected override void OnDispatcherUnhandledException(System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "DarkSword Restore", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }
}
