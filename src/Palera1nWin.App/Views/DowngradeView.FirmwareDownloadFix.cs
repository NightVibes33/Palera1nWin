using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Windows;
using Microsoft.Win32;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private FirmwareResumeState? _firmwareResumeState;

    protected override void OnInitialized(EventArgs e)
    {
        base.OnInitialized(e);

        // Replace the original one-shot downloader before the user can click it.
        // Dependency-backed experience services are deferred until Loaded because
        // WPF raises OnInitialized while InitializeComponent is still running.
        DownloadFirmwareButton.Click -= DownloadFirmware_Click;
        DownloadFirmwareButton.Click += DownloadFirmwareSafe_Click;
        Loaded += DeferredDowngradeExperience_Loaded;
    }

    private async void DownloadFirmwareSafe_Click(object sender, RoutedEventArgs e)
    {
        if (FirmwareList.SelectedItem is not FirmwareListItem firmware ||
            _detectedDarkSwordDevice is null ||
            !string.Equals(firmware.Identifier, _firmwareIdentifier, StringComparison.Ordinal))
        {
            return;
        }

        string destination;
        var canResumeKnownDestination = _firmwareResumeState is not null &&
                                        _firmwareResumeState.Matches(firmware) &&
                                        File.Exists(_firmwareResumeState.PartialPath);
        if (canResumeKnownDestination)
        {
            destination = _firmwareResumeState!.Destination;
        }
        else
        {
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
            destination = dialog.FileName;
        }

        _firmwareDownloadCts?.Cancel();
        _firmwareDownloadCts?.Dispose();
        _firmwareDownloadCts = new CancellationTokenSource();
        var cancellationToken = _firmwareDownloadCts.Token;
        var temporary = destination + ".partial";
        var existingLength = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
        _firmwareResumeState = new FirmwareResumeState(
            firmware.Identifier,
            firmware.Version,
            firmware.BuildId,
            firmware.Url,
            destination,
            temporary);

        FirmwareDownloadProgress.Visibility = Visibility.Visible;
        FirmwareDownloadProgress.IsIndeterminate = false;
        FirmwareDownloadProgress.Value = 0;
        FirmwareDownloadStatusText.Text = existingLength > 0
            ? $"Resuming {firmware.Version} from {FormatBytes(existingLength)}..."
            : $"Downloading {firmware.Version} ({firmware.BuildId}) for {firmware.Identifier}...";
        DownloadFirmwareButton.Content = existingLength > 0 ? "Resuming..." : "Downloading...";
        UpdateFirmwareSelectionState();

        try
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            HttpResponseMessage response;
            var append = false;

            if (existingLength > 0)
            {
                using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, firmware.Url);
                rangeRequest.Headers.Range = new RangeHeaderValue(existingLength, null);
                response = await FirmwareHttpClient.SendAsync(
                    rangeRequest,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (response.StatusCode == HttpStatusCode.PartialContent)
                {
                    append = true;
                    await AppendExistingFileToHashAsync(temporary, hash, cancellationToken);
                }
                else
                {
                    response.Dispose();
                    existingLength = 0;
                    append = false;
                    TryDelete(temporary);
                    response = await FirmwareHttpClient.GetAsync(
                        firmware.Url,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken);
                }
            }
            else
            {
                response = await FirmwareHttpClient.GetAsync(
                    firmware.Url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }

            using (response)
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentRange?.Length ??
                            (response.Content.Headers.ContentLength is { } contentLength
                                ? contentLength + (append ? existingLength : 0)
                                : firmware.FileSize);

                await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
                await using var output = new FileStream(
                    temporary,
                    append ? FileMode.Append : FileMode.Create,
                    FileAccess.Write,
                    FileShare.None,
                    1024 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);

                var buffer = new byte[1024 * 1024];
                long written = append ? existingLength : 0;
                while (true)
                {
                    var read = await input.ReadAsync(buffer, cancellationToken);
                    if (read == 0) break;

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
            }

            FirmwareDownloadProgress.IsIndeterminate = false;
            var actualSha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
            if (!string.IsNullOrWhiteSpace(firmware.Sha1) &&
                !actualSha1.Equals(firmware.Sha1, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    $"SHA-1 verification failed. Expected {firmware.Sha1}, downloaded {actualSha1}. The partial file was kept for diagnostics but must not be used.");
            }

            File.Move(temporary, destination, overwrite: true);
            _firmwareResumeState = null;
            IpswPathBox.Text = destination;
            _inspection = null;
            InvalidatePreflight("A new firmware download completed. Inspect it and run preflight.");
            IpswSummaryText.Text = $"Downloaded and SHA-1 verified for {firmware.Identifier}. Inspecting the IPSW now...";
            FirmwareDownloadProgress.Value = 100;
            FirmwareDownloadStatusText.Text = $"Download complete and verified: {destination}";
            DownloadFirmwareButton.Content = "Download & Use IPSW";
            AppendLog($"Downloaded IPSW {firmware.Identifier} {firmware.Version} {firmware.BuildId} to {destination}; SHA1={actualSha1}");
            InspectIpsw_Click(InspectIpswButton, new RoutedEventArgs());
        }
        catch (OperationCanceledException)
        {
            var saved = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
            FirmwareDownloadStatusText.Text =
                $"Download paused at {FormatBytes(saved)}. Press Resume Download to continue without restarting.";
            DownloadFirmwareButton.Content = "Resume Download";
            AppendLog($"Firmware download paused; partial preserved at {temporary} ({saved} bytes).");
        }
        catch (Exception exception)
        {
            var saved = File.Exists(temporary) ? new FileInfo(temporary).Length : 0;
            FirmwareDownloadStatusText.Text =
                $"Download interrupted: {exception.Message} Partial preserved at {FormatBytes(saved)}; press Retry / Resume.";
            DownloadFirmwareButton.Content = saved > 0 ? "Retry / Resume" : "Retry Download";
            AppendLog($"Firmware download failed with resumable state preserved: {exception}");
        }
        finally
        {
            _firmwareDownloadCts?.Dispose();
            _firmwareDownloadCts = null;
            FirmwareDownloadProgress.IsIndeterminate = false;
            UpdateFirmwareSelectionState();
            RefreshEnhancedActionState();
        }
    }

    private static async Task AppendExistingFileToHashAsync(
        string path,
        IncrementalHash hash,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            1024 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        var buffer = new byte[1024 * 1024];
        while (true)
        {
            var read = await stream.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            hash.AppendData(buffer, 0, read);
        }
    }

    private sealed record FirmwareResumeState(
        string Identifier,
        string Version,
        string BuildId,
        string Url,
        string Destination,
        string PartialPath)
    {
        public bool Matches(FirmwareListItem item) =>
            string.Equals(Identifier, item.Identifier, StringComparison.Ordinal) &&
            string.Equals(Version, item.Version, StringComparison.Ordinal) &&
            string.Equals(BuildId, item.BuildId, StringComparison.Ordinal) &&
            string.Equals(Url, item.Url, StringComparison.Ordinal);
    }
}
