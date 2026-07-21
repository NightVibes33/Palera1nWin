using System.IO;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // Replace the first implementation before the user can click it. Windows
        // will not rename a FileShare.None stream while that stream is still open.
        DownloadFirmwareButton.Click -= DownloadFirmware_Click;
        DownloadFirmwareButton.Click += DownloadFirmwareSafe_Click;
    }

    private async void DownloadFirmwareSafe_Click(object sender, RoutedEventArgs e)
    {
        if (FirmwareList.SelectedItem is not FirmwareListItem firmware ||
            _detectedDarkSwordDevice is null ||
            !string.Equals(firmware.Identifier, _firmwareIdentifier, StringComparison.Ordinal))
        {
            return;
        }

        var downloadDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Downloads",
            "DarkSword Firmware");
        Directory.CreateDirectory(downloadDirectory);

        var dialog = new SaveFileDialog
        {
            Title = $"Save {firmware.Identifier} iOS/iPadOS {firmware.Version}",
            InitialDirectory = downloadDirectory,
            FileName = firmware.FileName,
            DefaultExt = ".ipsw",
            Filter = "Apple firmware (*.ipsw)|*.ipsw",
            AddExtension = true,
            OverwritePrompt = true
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true)
        {
            return;
        }

        _firmwareDownloadCts?.Cancel();
        _firmwareDownloadCts?.Dispose();
        _firmwareDownloadCts = new CancellationTokenSource();
        var cancellationToken = _firmwareDownloadCts.Token;
        var destination = dialog.FileName;
        var temporary = destination + ".partial";

        FirmwareDownloadProgress.Visibility = Visibility.Visible;
        FirmwareDownloadProgress.Value = 0;
        FirmwareDownloadStatusText.Text = $"Downloading {firmware.Version} ({firmware.BuildId}) for {firmware.Identifier}...";
        UpdateFirmwareSelectionState();

        try
        {
            string actualSha1;
            using (var response = await FirmwareHttpClient.GetAsync(
                       firmware.Url,
                       HttpCompletionOption.ResponseHeadersRead,
                       cancellationToken))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength ?? firmware.FileSize;

                await using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
                await using (var output = new FileStream(
                                 temporary,
                                 FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 1024 * 1024,
                                 FileOptions.Asynchronous | FileOptions.SequentialScan))
                using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1))
                {
                    var buffer = new byte[1024 * 1024];
                    long written = 0;
                    while (true)
                    {
                        var read = await input.ReadAsync(buffer, cancellationToken);
                        if (read == 0)
                        {
                            break;
                        }

                        await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                        hash.AppendData(buffer, 0, read);
                        written += read;

                        if (total > 0)
                        {
                            var percent = Math.Clamp(written * 100d / total, 0, 100);
                            FirmwareDownloadProgress.Value = percent;
                            FirmwareDownloadStatusText.Text =
                                $"Downloading {firmware.Version}: {percent:F1}% • {FormatBytes(written)} of {FormatBytes(total)}";
                        }
                        else
                        {
                            FirmwareDownloadProgress.IsIndeterminate = true;
                            FirmwareDownloadStatusText.Text = $"Downloading {firmware.Version}: {FormatBytes(written)}";
                        }
                    }

                    await output.FlushAsync(cancellationToken);
                    actualSha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
                }
            }

            // Both input and output streams are closed before the atomic final rename.
            FirmwareDownloadProgress.IsIndeterminate = false;
            if (!string.IsNullOrWhiteSpace(firmware.Sha1) &&
                !actualSha1.Equals(firmware.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-1 verification failed. Expected {firmware.Sha1}, downloaded {actualSha1}.");
            }

            File.Move(temporary, destination, overwrite: true);
            IpswPathBox.Text = destination;
            _inspection = null;
            IpswSummaryText.Text = $"Downloaded and SHA-1 verified for {firmware.Identifier}. Inspecting the IPSW now...";
            FirmwareDownloadProgress.Value = 100;
            FirmwareDownloadStatusText.Text = $"Download complete: {destination}";
            AppendLog($"Downloaded IPSW {firmware.Identifier} {firmware.Version} {firmware.BuildId} to {destination}; SHA1={actualSha1}");
            InspectIpsw_Click(InspectIpswButton, new RoutedEventArgs());
        }
        catch (OperationCanceledException)
        {
            FirmwareDownloadStatusText.Text = "Firmware download cancelled.";
            TryDelete(temporary);
        }
        catch (Exception exception)
        {
            FirmwareDownloadStatusText.Text = $"Firmware download failed: {exception.Message}";
            TryDelete(temporary);
            AppendLog($"Firmware download failed: {exception}");
        }
        finally
        {
            _firmwareDownloadCts?.Dispose();
            _firmwareDownloadCts = null;
            FirmwareDownloadProgress.IsIndeterminate = false;
            UpdateFirmwareSelectionState();
        }
    }
}
