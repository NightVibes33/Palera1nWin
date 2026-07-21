using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using DarkSwordRestore.Core;
using Microsoft.Win32;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private static readonly HttpClient FirmwareHttpClient = CreateFirmwareHttpClient();
    private static readonly JsonSerializerOptions FirmwareJsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private static readonly Regex ProductTypePattern = new(
        @"\b(?:iPhone|iPad|iPod)\d+,\d+\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly SemaphoreSlim DeviceCatalogLock = new(1, 1);
    private static IReadOnlyList<ApiDevice>? CachedApiDevices;

    private readonly ObservableCollection<FirmwareListItem> _firmwareItems = [];
    private CancellationTokenSource? _firmwareLoadCts;
    private CancellationTokenSource? _firmwareDownloadCts;
    private CancellationTokenSource? _dfuGuideCts;
    private bool _firmwareFeaturesStarted;
    private string? _firmwareIdentifier;
    private DarkSwordDevice? _detectedDarkSwordDevice;

    private string? DetectedProductType => _firmwareIdentifier;

    private async void FirmwareFeatures_Loaded(object sender, RoutedEventArgs e)
    {
        if (_firmwareFeaturesStarted || _disposed)
        {
            return;
        }

        _firmwareFeaturesStarted = true;
        FirmwareList.ItemsSource = _firmwareItems;
        SupportedDevicesText.Text = BuildSupportedDeviceText();
        _monitor.DeviceChanged += FirmwareFeatures_DeviceChanged;

        try
        {
            await HandleFirmwareSnapshotAsync(await _monitor.ProbeAsync(), forceReload: true);
        }
        catch (Exception exception)
        {
            FirmwareDownloadStatusText.Text = $"Device detection failed: {exception.Message}";
            AppendLog($"Firmware feature initialization failed: {exception}");
        }
    }

    private void FirmwareFeatures_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(async () => await HandleFirmwareSnapshotAsync(snapshot, forceReload: false));

    private async void RefreshFirmware_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await HandleFirmwareSnapshotAsync(await _monitor.ProbeAsync(), forceReload: true);
        }
        catch (Exception exception)
        {
            FirmwareDownloadStatusText.Text = $"Refresh failed: {exception.Message}";
            AppendLog($"Firmware refresh failed: {exception}");
        }
    }

    private async Task HandleFirmwareSnapshotAsync(AppleDeviceSnapshot snapshot, bool forceReload)
    {
        if (_disposed)
        {
            return;
        }

        if (snapshot.Mode == AppleDeviceMode.Disconnected)
        {
            SetDetectedDevice(null, null, "Connect a supported A9-A10X iPhone, iPad, or iPod touch.");
            return;
        }

        RefreshFirmwareButton.IsEnabled = false;
        FirmwareDownloadStatusText.Text = "Reading the exact ProductType from the connected device...";

        try
        {
            var productType = await ResolveConnectedProductTypeAsync(snapshot, CancellationToken.None);
            if (productType is null)
            {
                SetDetectedDevice(
                    null,
                    null,
                    "Apple hardware is connected, but ProductType could not be resolved. Unlock and trust it in normal mode, or keep it connected in recovery/DFU and press Refresh.");
                return;
            }

            var device = DarkSwordDeviceCatalog.Find(productType);
            if (device is null)
            {
                SetDetectedDevice(
                    productType,
                    null,
                    $"{productType} is not in the supported A9-A10X iOS/iPadOS device range. No firmware is offered.");
                return;
            }

            var changed = !string.Equals(_firmwareIdentifier, productType, StringComparison.Ordinal);
            SetDetectedDevice(productType, device, null, clearFirmware: changed);
            if (changed || forceReload || _firmwareItems.Count == 0)
            {
                await LoadFirmwareCatalogAsync(productType);
            }
            else
            {
                FirmwareDownloadStatusText.Text = $"{_firmwareItems.Count} exact-device iOS/iPadOS 15 IPSWs loaded.";
            }
        }
        finally
        {
            RefreshFirmwareButton.IsEnabled = true;
            UpdateFirmwareSelectionState();
            UpdateActionState();
        }
    }

    private void SetDetectedDevice(
        string? productType,
        DarkSwordDevice? device,
        string? status,
        bool clearFirmware = true)
    {
        _firmwareIdentifier = productType;
        _detectedDarkSwordDevice = device;

        if (clearFirmware)
        {
            _firmwareLoadCts?.Cancel();
            _firmwareItems.Clear();
            FirmwareList.SelectedItem = null;
        }

        if (device is null)
        {
            FirmwareDeviceText.Text = productType is null ? "No exact supported device detected" : $"Detected {productType}";
            FirmwareSupportText.Text = status ?? "Connect a supported A9-A10X device.";
            FirmwareDownloadStatusText.Text = status ?? "Waiting for a supported device";
            RestoreCapabilityText.Text = "The restore button stays disabled until a supported exact ProductType and matching IPSW are verified.";
        }
        else
        {
            FirmwareDeviceText.Text = $"{device.DisplayName} • {device.ProductType} • {device.Chip}";
            FirmwareSupportText.Text = $"Exact-device mode: only iOS/iPadOS 15 IPSWs whose API identifier is {device.ProductType} are listed.";
            FirmwareDownloadStatusText.Text = status ?? "Loading firmware catalog...";
            RestoreCapabilityText.Text = device.UsesA9SepBlocks
                ? $"{device.Chip} Windows path active: SHC capture, tethered restore, PTE generation, and tether boot."
                : $"{device.Chip} is supported by turdus merula. Downloader, IPSW verification, and DFU guidance are active; the separate A10/A10X iBoot/SEP Windows tether-boot backend is not enabled yet, so the restore button remains blocked.";
        }

        UpdateDfuGuide(device);
        UpdateFirmwareSelectionState();
        UpdateActionState();
    }

    private async Task LoadFirmwareCatalogAsync(string productType)
    {
        _firmwareLoadCts?.Cancel();
        _firmwareLoadCts?.Dispose();
        _firmwareLoadCts = new CancellationTokenSource();
        var cancellationToken = _firmwareLoadCts.Token;

        FirmwareDownloadStatusText.Text = $"Loading signed and unsigned iOS/iPadOS 15 IPSWs for {productType}...";
        _firmwareItems.Clear();

        try
        {
            using var response = await FirmwareHttpClient.GetAsync(
                $"https://api.ipsw.me/v4/ipsw/device/{Uri.EscapeDataString(productType)}",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var catalog = await JsonSerializer.DeserializeAsync<DeviceFirmwareCatalog>(
                stream,
                FirmwareJsonOptions,
                cancellationToken)
                ?? throw new InvalidDataException("The IPSW catalog response was empty.");

            if (!string.Equals(catalog.Identifier, productType, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The firmware service returned {catalog.Identifier ?? "an unknown device"} instead of {productType}.");
            }

            var firmwares = catalog.Firmwares
                .Where(firmware => string.Equals(firmware.Identifier, productType, StringComparison.Ordinal))
                .Where(firmware => firmware.Version.StartsWith("15.", StringComparison.Ordinal))
                .Where(firmware => Uri.TryCreate(firmware.Url, UriKind.Absolute, out _))
                .OrderByDescending(firmware => ParseVersion(firmware.Version))
                .ThenByDescending(firmware => firmware.ReleaseDate ?? firmware.UploadDate ?? DateTimeOffset.MinValue)
                .Select(FirmwareListItem.FromApi)
                .ToArray();

            if (!string.Equals(_firmwareIdentifier, productType, StringComparison.Ordinal))
            {
                return;
            }

            foreach (var firmware in firmwares)
            {
                _firmwareItems.Add(firmware);
            }

            FirmwareDownloadStatusText.Text = firmwares.Length == 0
                ? $"No iOS/iPadOS 15 IPSWs were returned for exact device {productType}."
                : $"Loaded {firmwares.Length} exact-device iOS/iPadOS 15 IPSWs. Signed and unsigned entries are both shown.";
            AppendLog($"Loaded {firmwares.Length} iOS/iPadOS 15 IPSWs for exact ProductType {productType}.");
        }
        catch (OperationCanceledException)
        {
            // A device change or explicit refresh replaced this request.
        }
        catch (Exception exception)
        {
            _firmwareItems.Clear();
            FirmwareDownloadStatusText.Text = $"Could not load IPSWs: {exception.Message}";
            AppendLog($"IPSW catalog request failed: {exception}");
        }
        finally
        {
            UpdateFirmwareSelectionState();
        }
    }

    private async Task<string?> ResolveConnectedProductTypeAsync(
        AppleDeviceSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        var fromSnapshot = NormalizeProductType(snapshot.ProductType);
        if (fromSnapshot is not null)
        {
            return fromSnapshot;
        }

        var ideviceInfo = Path.Combine(_tools.Root, "ideviceinfo.exe");
        if (File.Exists(ideviceInfo))
        {
            var output = await RunIdentityToolAsync(ideviceInfo, ["-k", "ProductType"], cancellationToken);
            var productType = NormalizeProductType(output);
            if (productType is not null)
            {
                return productType;
            }
        }

        var irecovery = Path.Combine(_tools.Root, "irecovery.exe");
        if (!File.Exists(irecovery))
        {
            return null;
        }

        var recoveryOutput = await RunIdentityToolAsync(irecovery, ["-q"], cancellationToken);
        var directProductType = NormalizeProductType(recoveryOutput);
        if (directProductType is not null)
        {
            return directProductType;
        }

        if (!TryReadRecoveryNumber(recoveryOutput, "CPID", out var cpid) ||
            !TryReadRecoveryNumber(recoveryOutput, "BDID", out var bdid))
        {
            return null;
        }

        var devices = await GetApiDevicesAsync(cancellationToken);
        return devices
            .Where(device => device.Boards.Any(board => board.Cpid == cpid && board.Bdid == bdid))
            .Select(device => device.Identifier)
            .FirstOrDefault(DarkSwordDeviceCatalog.IsSupported);
    }

    private static async Task<IReadOnlyList<ApiDevice>> GetApiDevicesAsync(CancellationToken cancellationToken)
    {
        if (CachedApiDevices is not null)
        {
            return CachedApiDevices;
        }

        await DeviceCatalogLock.WaitAsync(cancellationToken);
        try
        {
            if (CachedApiDevices is not null)
            {
                return CachedApiDevices;
            }

            using var response = await FirmwareHttpClient.GetAsync(
                "https://api.ipsw.me/v4/devices",
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            CachedApiDevices = await JsonSerializer.DeserializeAsync<List<ApiDevice>>(
                stream,
                FirmwareJsonOptions,
                cancellationToken) ?? [];
            return CachedApiDevices;
        }
        finally
        {
            DeviceCatalogLock.Release();
        }
    }

    private static bool TryReadRecoveryNumber(string output, string key, out int value)
    {
        value = 0;
        var match = Regex.Match(
            output,
            $@"(?im)^\s*{Regex.Escape(key)}\s*:\s*(?<value>(?:0x)?[0-9a-f]+)\s*$",
            RegexOptions.CultureInvariant);
        if (!match.Success)
        {
            return false;
        }

        var text = match.Groups["value"].Value;
        var style = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? NumberStyles.AllowHexSpecifier
            : NumberStyles.Integer;
        if (style == NumberStyles.AllowHexSpecifier)
        {
            text = text[2..];
        }
        else if (text.Any(character => character is >= 'a' and <= 'f' or >= 'A' and <= 'F'))
        {
            style = NumberStyles.AllowHexSpecifier;
        }

        return int.TryParse(text, style, CultureInfo.InvariantCulture, out value);
    }

    private static string? NormalizeProductType(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var match = ProductTypePattern.Match(text);
        if (!match.Success)
        {
            return null;
        }

        var value = match.Value;
        if (value.StartsWith("iPhone", StringComparison.OrdinalIgnoreCase)) return "iPhone" + value[6..];
        if (value.StartsWith("iPad", StringComparison.OrdinalIgnoreCase)) return "iPad" + value[4..];
        if (value.StartsWith("iPod", StringComparison.OrdinalIgnoreCase)) return "iPod" + value[4..];
        return value;
    }

    private async Task<string> RunIdentityToolAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = _tools.Root,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = Process.Start(startInfo);
        if (process is null)
        {
            return string.Empty;
        }

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);
        try
        {
            var stdoutTask = process.StandardOutput.ReadToEndAsync(linkedCts.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(linkedCts.Token);
            await process.WaitForExitAsync(linkedCts.Token);
            return (await stdoutTask) + Environment.NewLine + (await stderrTask);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!process.HasExited) process.Kill(entireProcessTree: true);
            }
            catch
            {
                // The probe may exit while timeout handling is running.
            }
            return string.Empty;
        }
    }

    private void FirmwareSelectionChanged(object sender, SelectionChangedEventArgs e) =>
        UpdateFirmwareSelectionState();

    private void UpdateFirmwareSelectionState()
    {
        DownloadFirmwareButton.IsEnabled =
            _firmwareDownloadCts is null &&
            FirmwareList.SelectedItem is FirmwareListItem selected &&
            _detectedDarkSwordDevice is not null &&
            string.Equals(selected.Identifier, _firmwareIdentifier, StringComparison.Ordinal);
        CancelFirmwareDownloadButton.IsEnabled = _firmwareDownloadCts is not null;
    }

    private async void DownloadFirmware_Click(object sender, RoutedEventArgs e)
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
            using var response = await FirmwareHttpClient.GetAsync(
                firmware.Url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();

            var total = response.Content.Headers.ContentLength ?? firmware.FileSize;
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temporary,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                1024 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);

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
            FirmwareDownloadProgress.IsIndeterminate = false;
            var actualSha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
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

    private void CancelFirmwareDownload_Click(object sender, RoutedEventArgs e) =>
        _firmwareDownloadCts?.Cancel();

    private void UpdateDfuGuide(DarkSwordDevice? device)
    {
        DfuGuideProgress.Value = 0;
        DfuGuideStatusText.Text = device is null
            ? "Ready when a supported device is detected"
            : "Ready. Keep the device connected directly to the PC.";

        if (device is null)
        {
            DfuGuideTitle.Text = "Connect a supported device";
            DfuGuideStepsText.Text =
                "The exact button sequence appears after ProductType detection. iPhone 7 and 7 Plus use Volume Down; other supported devices use Home.";
            StartDfuGuideButton.IsEnabled = false;
            return;
        }

        DfuGuideTitle.Text = $"DFU guide for {device.DisplayName} ({device.ProductType})";
        DfuGuideStepsText.Text = device.DfuProfile == DfuButtonProfile.VolumeDown
            ? "1. Keep the iPhone connected. Power it off if possible.\n2. Hold Side + Volume Down together for 8 seconds.\n3. Release Side but keep holding Volume Down for about 10 seconds.\n4. The screen must stay black. A cable/computer image is Recovery Mode, not DFU."
            : "1. Keep the device connected. Power it off if possible.\n2. Hold Top/Side + Home together for 8 seconds.\n3. Release Top/Side but keep holding Home for about 10 seconds.\n4. The screen must stay black. A cable/computer image is Recovery Mode, not DFU.";
        StartDfuGuideButton.IsEnabled = _dfuGuideCts is null;
    }

    private async void StartDfuGuide_Click(object sender, RoutedEventArgs e)
    {
        var device = _detectedDarkSwordDevice;
        if (device is null)
        {
            return;
        }

        _dfuGuideCts?.Cancel();
        _dfuGuideCts?.Dispose();
        _dfuGuideCts = new CancellationTokenSource();
        var cancellationToken = _dfuGuideCts.Token;
        StartDfuGuideButton.IsEnabled = false;
        CancelDfuGuideButton.IsEnabled = true;
        DfuGuideProgress.Value = 0;

        try
        {
            var waitForDfu = _monitor.WaitForModeAsync(
                [AppleDeviceMode.Dfu],
                TimeSpan.FromSeconds(40),
                cancellationToken);

            if (await RunDfuCountdownStepAsync(
                    "Get ready. Keep the cable connected",
                    3,
                    0,
                    10,
                    waitForDfu,
                    cancellationToken))
            {
                SetDfuGuideSuccess();
                return;
            }

            var firstButtons = device.DfuProfile == DfuButtonProfile.VolumeDown
                ? "HOLD SIDE + VOLUME DOWN"
                : "HOLD TOP/SIDE + HOME";
            if (await RunDfuCountdownStepAsync(
                    firstButtons,
                    8,
                    10,
                    52,
                    waitForDfu,
                    cancellationToken))
            {
                SetDfuGuideSuccess();
                return;
            }

            var secondButton = device.DfuProfile == DfuButtonProfile.VolumeDown
                ? "RELEASE SIDE — KEEP HOLDING VOLUME DOWN"
                : "RELEASE TOP/SIDE — KEEP HOLDING HOME";
            if (await RunDfuCountdownStepAsync(
                    secondButton,
                    10,
                    52,
                    95,
                    waitForDfu,
                    cancellationToken))
            {
                SetDfuGuideSuccess();
                return;
            }

            DfuGuideStatusText.Text = "Waiting for Windows to enumerate Apple DFU mode...";
            await waitForDfu;
            SetDfuGuideSuccess();
        }
        catch (OperationCanceledException)
        {
            DfuGuideStatusText.Text = "DFU guide cancelled.";
            DfuGuideProgress.Value = 0;
        }
        catch (TimeoutException)
        {
            DfuGuideStatusText.Text =
                "DFU was not detected. Force-restart the device, verify the cable, and retry. A visible recovery screen is not DFU.";
            DfuGuideProgress.Value = 0;
        }
        catch (Exception exception)
        {
            DfuGuideStatusText.Text = $"DFU guide stopped: {exception.Message}";
            AppendLog($"DFU guide failed: {exception}");
        }
        finally
        {
            _dfuGuideCts?.Dispose();
            _dfuGuideCts = null;
            CancelDfuGuideButton.IsEnabled = false;
            StartDfuGuideButton.IsEnabled = _detectedDarkSwordDevice is not null;
        }
    }

    private async Task<bool> RunDfuCountdownStepAsync(
        string label,
        int seconds,
        double progressStart,
        double progressEnd,
        Task<AppleDeviceSnapshot> waitForDfu,
        CancellationToken cancellationToken)
    {
        for (var remaining = seconds; remaining >= 1; remaining--)
        {
            if (waitForDfu.IsCompletedSuccessfully)
            {
                return true;
            }

            DfuGuideStatusText.Text = $"{label} — {remaining}";
            var completed = seconds - remaining;
            DfuGuideProgress.Value = progressStart + (progressEnd - progressStart) * completed / seconds;
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
        }

        DfuGuideProgress.Value = progressEnd;
        return waitForDfu.IsCompletedSuccessfully;
    }

    private void SetDfuGuideSuccess()
    {
        DfuGuideStatusText.Text = "DFU mode detected. The screen should be completely black.";
        DfuGuideProgress.Value = 100;
        AppendLog($"Guided DFU entry succeeded for {_firmwareIdentifier ?? "unknown ProductType"}.");
    }

    private void CancelDfuGuide_Click(object sender, RoutedEventArgs e) =>
        _dfuGuideCts?.Cancel();

    private bool IsActiveRestoreTargetReady()
    {
        if (_detectedDarkSwordDevice?.UsesA9SepBlocks != true ||
            string.IsNullOrWhiteSpace(_firmwareIdentifier) ||
            _inspection?.IsValid != true)
        {
            return false;
        }

        return _inspection.MatchesProductType(_firmwareIdentifier) &&
               _inspection.ProductVersion?.StartsWith("15.", StringComparison.Ordinal) == true;
    }

    private bool IsActiveA9TetherBootTarget() =>
        _detectedDarkSwordDevice?.UsesA9SepBlocks == true;

    private void DisposeFirmwareFeatures()
    {
        if (_firmwareFeaturesStarted)
        {
            _monitor.DeviceChanged -= FirmwareFeatures_DeviceChanged;
            _firmwareFeaturesStarted = false;
        }

        _firmwareLoadCts?.Cancel();
        _firmwareLoadCts?.Dispose();
        _firmwareLoadCts = null;
        _firmwareDownloadCts?.Cancel();
        _firmwareDownloadCts?.Dispose();
        _firmwareDownloadCts = null;
        _dfuGuideCts?.Cancel();
        _dfuGuideCts?.Dispose();
        _dfuGuideCts = null;
    }

    private static HttpClient CreateFirmwareHttpClient()
    {
        var client = new HttpClient
        {
            Timeout = TimeSpan.FromHours(6)
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("DarkSword-Restore", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return client;
    }

    private static Version ParseVersion(string value) =>
        Version.TryParse(value, out var version) ? version : new Version(0, 0);

    private static string BuildSupportedDeviceText()
    {
        var phones = DarkSwordDeviceCatalog.All
            .Where(device => !device.ProductType.StartsWith("iPad", StringComparison.Ordinal))
            .GroupBy(device => new { device.DisplayName, device.Chip })
            .Select(group => $"• {group.Key.DisplayName} — {group.Key.Chip} — {string.Join(", ", group.Select(item => item.ProductType))}");
        var ipads = DarkSwordDeviceCatalog.All
            .Where(device => device.ProductType.StartsWith("iPad", StringComparison.Ordinal))
            .GroupBy(device => new { device.DisplayName, device.Chip })
            .Select(group => $"• {group.Key.DisplayName} — {group.Key.Chip} — {string.Join(", ", group.Select(item => item.ProductType))}");

        return "iPhone and iPod touch\n" + string.Join(Environment.NewLine, phones) +
               "\n\niPad\n" + string.Join(Environment.NewLine, ipads) +
               "\n\nA11 and newer devices are intentionally rejected.";
    }

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double size = value;
        var unit = 0;
        while (size >= 1024 && unit < units.Length - 1)
        {
            size /= 1024;
            unit++;
        }
        return $"{size:F2} {units[unit]}";
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Cleanup is best-effort; the partial file can be removed manually.
        }
    }

    private sealed record DeviceFirmwareCatalog(
        [property: JsonPropertyName("identifier")] string? Identifier,
        [property: JsonPropertyName("name")] string? Name,
        [property: JsonPropertyName("firmwares")] IReadOnlyList<ApiFirmware> Firmwares)
    {
        public IReadOnlyList<ApiFirmware> Firmwares { get; init; } = Firmwares ?? [];
    }

    private sealed record ApiFirmware(
        [property: JsonPropertyName("identifier")] string Identifier,
        [property: JsonPropertyName("version")] string Version,
        [property: JsonPropertyName("buildid")] string BuildId,
        [property: JsonPropertyName("sha1sum")] string? Sha1,
        [property: JsonPropertyName("filesize")] long FileSize,
        [property: JsonPropertyName("url")] string Url,
        [property: JsonPropertyName("releasedate")] DateTimeOffset? ReleaseDate,
        [property: JsonPropertyName("uploaddate")] DateTimeOffset? UploadDate,
        [property: JsonPropertyName("signed")] bool Signed);

    private sealed record ApiDevice(
        [property: JsonPropertyName("identifier")] string Identifier,
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("boards")] IReadOnlyList<ApiBoard> Boards)
    {
        public IReadOnlyList<ApiBoard> Boards { get; init; } = Boards ?? [];
    }

    private sealed record ApiBoard(
        [property: JsonPropertyName("boardconfig")] string BoardConfig,
        [property: JsonPropertyName("cpid")] int Cpid,
        [property: JsonPropertyName("bdid")] int Bdid);

    private sealed record FirmwareListItem(
        string Identifier,
        string Version,
        string BuildId,
        bool Signed,
        DateTimeOffset? ReleaseDate,
        long FileSize,
        string Url,
        string? Sha1)
    {
        public string SigningStatus => Signed ? "Signed" : "Unsigned";
        public string ReleaseDateText => ReleaseDate?.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "Unknown";
        public string SizeText => FileSize > 0 ? FormatBytes(FileSize) : "Unknown";
        public string FileName
        {
            get
            {
                if (Uri.TryCreate(Url, UriKind.Absolute, out var uri))
                {
                    var name = Uri.UnescapeDataString(Path.GetFileName(uri.LocalPath));
                    if (name.EndsWith(".ipsw", StringComparison.OrdinalIgnoreCase))
                    {
                        return name;
                    }
                }
                return $"{Identifier}_{Version}_{BuildId}.ipsw";
            }
        }

        public static FirmwareListItem FromApi(ApiFirmware firmware) =>
            new(
                firmware.Identifier,
                firmware.Version,
                firmware.BuildId,
                firmware.Signed,
                firmware.ReleaseDate ?? firmware.UploadDate,
                firmware.FileSize,
                firmware.Url,
                firmware.Sha1);
    }
}
