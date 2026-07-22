using System.IO;
using System.Windows;
using DarkSwordRestore.Core;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private readonly RedactedSessionExportService _redactedExportService = new();
    private bool _safeExportOverridesInitialized;

    private void InitializeSafeExportOverrides()
    {
        if (_safeExportOverridesInitialized || !_operationalExperienceInitialized) return;
        _safeExportOverridesInitialized = true;

        _exportButton.Click -= ExportLatest_Click;
        _exportButton.Click += ExportRedactedLatest_Click;
        PostDowngradePanel.IsVisibleChanged -= Operational_PostPanelChanged;
        PostDowngradePanel.IsVisibleChanged += SafeOperational_PostPanelChanged;
    }

    private async void SafeOperational_PostPanelChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (PostDowngradePanel.Visibility == Visibility.Visible && _completedSession is not null)
            await SaveProfileAndRedactedExportAsync(_completedSession, automatic: true);
    }

    private async void ExportRedactedLatest_Click(object sender, RoutedEventArgs e)
    {
        var session = _completedSession ?? _recoveryCandidate?.Session ?? await FindLatestSessionAsync();
        if (session is null)
        {
            ShowMessage("No downgrade session is available to export.", "Redacted support export", MessageBoxImage.Information);
            return;
        }

        var owner = Window.GetWindow(this);
        var confirmed = MessageBox.Show(
            owner,
            "Create a redacted diagnostic ZIP?\n\nThe bundle excludes raw SHC/PTE payloads, boot-profile.json, IPSWs, ECID, USB instance IDs, and absolute local paths. It includes a redacted log tail, firmware hashes, stage state, and artifact hash summaries. Review it before sharing.",
            "Redacted support export",
            MessageBoxButton.YesNo,
            MessageBoxImage.Information,
            MessageBoxResult.Yes);
        if (confirmed != MessageBoxResult.Yes) return;

        await SaveProfileAndRedactedExportAsync(session, automatic: false);
        ShowMessage(
            "A redacted diagnostic ZIP was saved. Raw boot assets were not copied.",
            "Redacted support export complete",
            MessageBoxImage.Information);
    }

    private async Task SaveProfileAndRedactedExportAsync(RestoreSession session, bool automatic)
    {
        if (automatic && string.Equals(_lastExportedSessionId, session.SessionId, StringComparison.Ordinal)) return;
        try
        {
            var snapshot = _monitor.Current;
            var productType = session.BoundProductType ?? DetectedProductType ??
                              session.Ipsw.SupportedProductTypes.FirstOrDefault() ?? "unknown";
            var device = DarkSwordDeviceCatalog.Find(productType);
            var ecid = session.BoundEcid ?? snapshot.Ecid;
            var profile = new DeviceDowngradeProfile(
                DeviceProfileStore.BuildKey(productType, ecid, snapshot.InstanceId),
                productType,
                device?.DisplayName ?? snapshot.DisplayName ?? "Apple device",
                ecid,
                snapshot.InstanceId,
                session.IpswPath,
                session.Ipsw.ProductVersion,
                session.Ipsw.BuildVersion,
                session.Ipsw.Sha256,
                session.PteBlockPath,
                session.SessionDirectory,
                DateTimeOffset.UtcNow);
            await _profileStore.SaveAsync(profile);
            _activeDeviceProfile = profile;

            var export = await _redactedExportService.ExportAsync(
                session,
                _logPath,
                profile,
                _cableTracker.GetSnapshot());
            _lastExportedSessionId = session.SessionId;
            _profile.Text = "Profile saved; redacted support ZIP created";
            _profileDetail.Text = export;
            AppendLog($"Saved exact-device profile and redacted support export: {export}");
        }
        catch (Exception exception)
        {
            _profile.Text = "Redacted support export failed";
            _profileDetail.Text = exception.Message;
            AppendLog($"Redacted support export failed: {exception}");
            if (!automatic) throw;
        }
    }

    private void DisposeSafeExportOverrides()
    {
        if (!_safeExportOverridesInitialized) return;
        _safeExportOverridesInitialized = false;
        _exportButton.Click -= ExportRedactedLatest_Click;
        PostDowngradePanel.IsVisibleChanged -= SafeOperational_PostPanelChanged;
    }
}
