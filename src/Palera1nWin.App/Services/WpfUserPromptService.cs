using Palera1nWin.App.Views;
using Palera1nWin.Core.Interaction;

namespace Palera1nWin.App.Services;

public sealed class WpfUserPromptService : IUserPromptService
{
    public async Task<bool> ConfirmAsync(UserPromptRequest request, CancellationToken cancellationToken = default)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return false;

        if (string.Equals(request.Title, "Ready for DFU mode?", StringComparison.Ordinal))
        {
            var guided = await JailbreakDfuVisualCoordinator.BeginFromNativePromptAsync(cancellationToken)
                .ConfigureAwait(false);
            if (guided.HasValue) return guided.Value;
        }

        return await app.Dispatcher.InvokeAsync(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            var result = System.Windows.MessageBox.Show(
                owner: app.MainWindow,
                messageBoxText: request.Message,
                caption: request.Title,
                button: System.Windows.MessageBoxButton.OKCancel,
                icon: System.Windows.MessageBoxImage.Information,
                defaultResult: System.Windows.MessageBoxResult.OK);

            return result == System.Windows.MessageBoxResult.OK;
        }, System.Windows.Threading.DispatcherPriority.Normal, cancellationToken).Task.ConfigureAwait(false);
    }
}
