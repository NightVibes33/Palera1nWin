using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Windows;
using System.Windows.Threading;
using Palera1nWin.App;
using Palera1nWin.App.Views;

namespace Palera1nWin.Core.Tests;

public sealed class DowngradeViewSmokeTests
{
    [Fact]
    public void OpeningDowngradeViewBuildsOperationalDashboardWithoutFatalException()
    {
        Exception? failure = null;
        using var finished = new ManualResetEventSlim();

        var thread = new Thread(() =>
        {
            App? app = null;
            Window? host = null;
            DowngradeView? view = null;
            try
            {
                app = new App();
                app.InitializeComponent();

                view = new DowngradeView();
                host = new Window
                {
                    Content = view,
                    Width = 1100,
                    Height = 800,
                    ShowInTaskbar = false,
                    WindowStyle = WindowStyle.None,
                };

                host.Show();
                view.Dispatcher.Invoke(() => { }, DispatcherPriority.ContextIdle);

                foreach (var fieldName in new[]
                         {
                             "_compatibility",
                             "_failure",
                             "_nextAction",
                             "_exportButton",
                         })
                {
                    var field = typeof(DowngradeView).GetField(
                        fieldName,
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    Assert.NotNull(field);
                    Assert.NotNull(field!.GetValue(view));
                }
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                try { host?.Close(); } catch { }
                try { view?.Dispose(); } catch { }
                try { app?.Shutdown(); } catch { }
                finished.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();

        Assert.True(finished.Wait(TimeSpan.FromSeconds(30)), "The Downgrade WPF smoke test did not finish.");
        if (failure is not null) ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
