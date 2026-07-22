using System.IO;
using System.Text.Json;
using System.Windows;
using DarkSwordRestore.Core;
using Palera1nWin.App.Services;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private const int ExactValidationSchema = 2;
    private readonly object _identityGuardSync = new();
    private ExactHardwareValidation? _guardIdentity;
    private int _identityGuardGeneration;
    private bool _identityMismatchReported;
    private DateTimeOffset _identityGuardStartedAt;

    private string ExactHardwareValidationPath => Path.Combine(
        _dataDirectory,
        "hardware",
        "pongo-validation-v2.json");

    private sealed record ExactHardwareValidation(
        int Schema,
        string ProductType,
        string Ecid,
        string? DfuInstanceId,
        string? DfuService,
        DateTimeOffset ValidatedAt,
        DateTimeOffset ExpiresAt);

    private async void ValidateExactHardware_Click(object sender, RoutedEventArgs e)
    {
        _operationCts?.Cancel();
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        var lease = await TryAcquireHardwareLeaseAsync(
            HardwareOperationKind.DriverRepair,
            "Testing one exact ECID through DFU, checkm8, PongoOS and the bridge",
            _operationCts.Token);
        if (lease is null)
        {
            _operationCts.Dispose();
            _operationCts = null;
            return;
        }

        SetBusy(true, "Test exact DFU → PongoOS", "No firmware is erased. The DFU ProductType and ECID are saved only after the complete Pongo bridge test passes.");
        try
        {
            var identityTask = _monitor.WaitForModeAsync(
                new[] { AppleDeviceMode.Dfu },
                TimeSpan.FromMinutes(5),
                _operationCts.Token);

            await _orchestrator.ValidateDfuToPongoAsync(
                new Progress<RestoreProgress>(UpdateProgress),
                AppendLog,
                _operationCts.Token);

            var dfuIdentity = await identityTask;
            if (!dfuIdentity.HasExactIdentity)
            {
                throw new DarkSwordException(
                    RestoreStage.Preflight,
                    "The non-destructive test reached PongoOS, but the clean DFU ProductType and ECID could not be read. The destructive gate remains locked.");
            }
            if (!string.IsNullOrWhiteSpace(DetectedProductType) &&
                !string.Equals(dfuIdentity.ProductType, DetectedProductType, StringComparison.Ordinal))
            {
                throw new DarkSwordException(
                    RestoreStage.Preflight,
                    $"The tested DFU target is {dfuIdentity.ProductType}, but the selected device is {DetectedProductType}.");
            }

            var receipt = new ExactHardwareValidation(
                ExactValidationSchema,
                dfuIdentity.ProductType!,
                dfuIdentity.NormalizedEcid!,
                dfuIdentity.InstanceId,
                dfuIdentity.Service,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddDays(7));
            await SaveExactHardwareValidationAsync(receipt, _operationCts.Token);

            // Keep the schema-1 marker temporarily for older UI-state code. Every
            // destructive/recovery click is additionally blocked by the schema-2 ECID receipt.
            await SaveHardwareValidationAsync();
            RefreshExactHardwareValidationUi();
            ShowMessage(
                $"The exact-device hardware gate passed.\n\nProductType: {receipt.ProductType}\nECID: {receipt.Ecid}\n\nNo firmware was erased.",
                "Exact hardware gate passed",
                MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            AppendLog("Exact-device hardware validation cancelled.");
        }
        catch (Exception exception)
        {
            TryDeleteExactHardwareValidation();
            AppendLog(exception.ToString());
            ShowMessage(exception.Message, "Exact hardware gate failed", MessageBoxImage.Error);
        }
        finally
        {
            await lease.DisposeAsync();
            _operationCts?.Dispose();
            _operationCts = null;
            SetBusy(false, CurrentStageText.Text, CurrentDetailText.Text);
            RefreshExactHardwareValidationUi();
        }
    }

    private async void StartIdentityBoundDowngrade_Click(object sender, RoutedEventArgs e)
    {
        var receipt = LoadCurrentExactHardwareValidation();
        if (receipt is null)
        {
            ShowMessage(
                "Run Test DFU → PongoOS again. The destructive restore requires an unexpired schema-2 receipt containing this device's exact ECID.",
                "Exact-device gate required",
                MessageBoxImage.Warning);
            return;
        }

        try
        {
            await StartIdentityGuardAsync(receipt);
        }
        catch (Exception exception)
        {
            ShowMessage(exception.Message, "Exact-device verification failed", MessageBoxImage.Error);
            return;
        }
        StartEnhancedDowngrade_Click(sender, e);
    }

    private async void ResumeIdentityBoundSession_Click(object sender, RoutedEventArgs e)
    {
        if (_recoveryCandidate is null) return;
        try
        {
            _recoveryCandidate = RecoveryIntegrityValidator.ValidateAndNormalize(_recoveryCandidate);
        }
        catch (Exception exception)
        {
            AppendLog($"Recovery integrity validation blocked the session: {exception}");
            ShowMessage(
                "Recovery was blocked before any USB or native operation:\n\n" + exception.Message,
                "Recovery artifact integrity failed",
                MessageBoxImage.Error);
            return;
        }

        var receipt = LoadCurrentExactHardwareValidation();
        var session = _recoveryCandidate.Session;
        if (session.HasBoundIdentity)
        {
            receipt = new ExactHardwareValidation(
                ExactValidationSchema,
                session.BoundProductType!,
                AppleDeviceSnapshot.NormalizeEcid(session.BoundEcid)!,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow.AddHours(1));
        }
        if (receipt is null)
        {
            ShowMessage(
                "This recovery session has no exact ECID binding and there is no current exact-device hardware receipt. Run the hardware test with the intended device before resuming.",
                "Recovery identity required",
                MessageBoxImage.Warning);
            return;
        }
        if (!session.Ipsw.MatchesProductType(receipt.ProductType))
        {
            ShowMessage(
                $"Recovery session {session.SessionId} does not target the receipt device {receipt.ProductType}.",
                "Recovery target mismatch",
                MessageBoxImage.Error);
            return;
        }

        session = session with
        {
            BoundProductType = receipt.ProductType,
            BoundEcid = receipt.Ecid,
            UpdatedAt = DateTimeOffset.UtcNow,
        };
        await _sessions.SaveAsync(session, CancellationToken.None);
        _recoveryCandidate = _recoveryCandidate with { Session = session };
        await StartIdentityGuardAsync(receipt);
        await ResumeLatestSessionAsync();
    }

    private async Task StartIdentityGuardAsync(ExactHardwareValidation receipt)
    {
        var current = await _monitor.ProbeAsync();
        if (current.HasExactIdentity && !current.MatchesIdentity(receipt.ProductType, receipt.Ecid))
        {
            throw new DarkSwordException(
                RestoreStage.Preflight,
                $"Connected device {current.ProductType} ECID {current.NormalizedEcid} does not match the validated target {receipt.ProductType} ECID {receipt.Ecid}.");
        }

        int generation;
        lock (_identityGuardSync)
        {
            _guardIdentity = receipt;
            _identityMismatchReported = false;
            _identityGuardStartedAt = DateTimeOffset.UtcNow;
            generation = ++_identityGuardGeneration;
        }
        _monitor.DeviceChanged -= ExactIdentityGuard_DeviceChanged;
        _monitor.DeviceChanged += ExactIdentityGuard_DeviceChanged;
        _ = TrackIdentityGuardLifetimeAsync(generation, receipt);
    }

    private void ExactIdentityGuard_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot)
    {
        ExactHardwareValidation? expected;
        lock (_identityGuardSync) expected = _guardIdentity;
        if (expected is null || !snapshot.HasExactIdentity) return;
        if (snapshot.MatchesIdentity(expected.ProductType, expected.Ecid)) return;

        lock (_identityGuardSync)
        {
            if (_identityMismatchReported) return;
            _identityMismatchReported = true;
        }
        AppendLog(
            $"SECURITY STOP: device identity changed to {snapshot.ProductType} ECID {snapshot.NormalizedEcid}; " +
            $"expected {expected.ProductType} ECID {expected.Ecid}. Cancelling before the next native stage.");
        _operationCts?.Cancel();
        Dispatcher.BeginInvoke(() => ShowMessage(
            "A different Apple device appeared during the operation. The active native stage was cancelled and the session was preserved.",
            "Exact-device safety stop",
            MessageBoxImage.Error));
    }

    private async Task TrackIdentityGuardLifetimeAsync(int generation, ExactHardwareValidation receipt)
    {
        var busyObserved = false;
        var deadline = DateTimeOffset.UtcNow.AddSeconds(30);
        try
        {
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Shell?.HardwareOperations.Current.IsBusy == true)
                {
                    busyObserved = true;
                    break;
                }
                await Task.Delay(150);
            }

            while (busyObserved && Shell?.HardwareOperations.Current.IsBusy == true)
            {
                await PersistIdentityIntoNewestSessionAsync(receipt);
                await Task.Delay(500);
            }
            await PersistIdentityIntoNewestSessionAsync(receipt);
        }
        catch (Exception exception)
        {
            AppendLog($"Exact-device session binding warning: {exception.Message}");
        }
        finally
        {
            lock (_identityGuardSync)
            {
                if (generation != _identityGuardGeneration) return;
                _guardIdentity = null;
            }
            _monitor.DeviceChanged -= ExactIdentityGuard_DeviceChanged;
        }
    }

    private async Task PersistIdentityIntoNewestSessionAsync(ExactHardwareValidation receipt)
    {
        if (!Directory.Exists(_sessions.RootDirectory)) return;
        var directory = Directory.EnumerateDirectories(_sessions.RootDirectory)
            .Where(path => Directory.GetCreationTimeUtc(path) >= _identityGuardStartedAt.UtcDateTime.AddMinutes(-1))
            .OrderByDescending(Directory.GetCreationTimeUtc)
            .FirstOrDefault();
        if (directory is null) return;
        var session = await _sessions.LoadAsync(directory, CancellationToken.None);
        if (session is null || !session.Ipsw.MatchesProductType(receipt.ProductType)) return;
        if (session.HasBoundIdentity)
        {
            if (!string.Equals(session.BoundProductType, receipt.ProductType, StringComparison.Ordinal) ||
                !string.Equals(AppleDeviceSnapshot.NormalizeEcid(session.BoundEcid), receipt.Ecid, StringComparison.OrdinalIgnoreCase))
            {
                _operationCts?.Cancel();
                throw new InvalidDataException("The active session was already bound to a different ECID.");
            }
            return;
        }
        await _sessions.SaveAsync(session with
        {
            BoundProductType = receipt.ProductType,
            BoundEcid = receipt.Ecid,
            UpdatedAt = DateTimeOffset.UtcNow,
        }, CancellationToken.None);
    }

    private async Task SaveExactHardwareValidationAsync(
        ExactHardwareValidation receipt,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(ExactHardwareValidationPath)!);
        var temporary = ExactHardwareValidationPath + ".tmp";
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(receipt, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }),
            cancellationToken);
        File.Move(temporary, ExactHardwareValidationPath, overwrite: true);
    }

    private ExactHardwareValidation? LoadCurrentExactHardwareValidation()
    {
        try
        {
            if (!File.Exists(ExactHardwareValidationPath)) return null;
            var receipt = JsonSerializer.Deserialize<ExactHardwareValidation>(
                File.ReadAllText(ExactHardwareValidationPath),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (receipt is null || receipt.Schema != ExactValidationSchema) return null;
            if (receipt.ExpiresAt < DateTimeOffset.UtcNow || receipt.ValidatedAt > DateTimeOffset.UtcNow.AddMinutes(5)) return null;
            if (string.IsNullOrWhiteSpace(receipt.ProductType) || string.IsNullOrWhiteSpace(receipt.Ecid)) return null;
            if (!string.IsNullOrWhiteSpace(DetectedProductType) &&
                !string.Equals(receipt.ProductType, DetectedProductType, StringComparison.Ordinal)) return null;
            return receipt with { Ecid = AppleDeviceSnapshot.NormalizeEcid(receipt.Ecid)! };
        }
        catch
        {
            return null;
        }
    }

    private void RefreshExactHardwareValidationUi()
    {
        if (!IsLoaded) return;
        var receipt = LoadCurrentExactHardwareValidation();
        HardwareValidationStatusText.Text = receipt is null
            ? "REQUIRED — run the exact-device DFU → PongoOS test. Destructive restore and recovery remain locked without ProductType + ECID."
            : $"PASSED — {receipt.ProductType} ECID {receipt.Ecid} was verified through PongoOS; expires {receipt.ExpiresAt.ToLocalTime():g}.";
        HardwareValidationStatusText.Foreground = ResourceBrush(receipt is null ? "Brush.Accent" : "Brush.Success");
    }

    private void ExactValidation_DeviceChanged(object? sender, AppleDeviceSnapshot snapshot) =>
        Dispatcher.BeginInvoke(RefreshExactHardwareValidationUi);

    private void TryDeleteExactHardwareValidation()
    {
        try { if (File.Exists(ExactHardwareValidationPath)) File.Delete(ExactHardwareValidationPath); }
        catch { }
    }

    private void DisposeExactIdentityGuard()
    {
        _monitor.DeviceChanged -= ExactIdentityGuard_DeviceChanged;
        _monitor.DeviceChanged -= ExactValidation_DeviceChanged;
        lock (_identityGuardSync)
        {
            _guardIdentity = null;
            _identityGuardGeneration++;
        }
    }
}
