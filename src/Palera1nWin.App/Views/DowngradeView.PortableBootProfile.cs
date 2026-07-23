using System.IO;
using System.Security.Cryptography;
using System.Windows;
using DarkSwordRestore.Core;
using Microsoft.Win32;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private bool _portableBootProfileOverrideInitialized;

    private void InitializePortableBootProfileOverride()
    {
        if (_portableBootProfileOverrideInitialized || _bootProfileBrowseButton is null) return;
        _portableBootProfileOverrideInitialized = true;
        _bootProfileBrowseButton.Click -= ImportBootProfile_Click;
        _bootProfileBrowseButton.Click += ImportPortableBootProfile_Click;
    }

    private async void ImportPortableBootProfile_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Import a copied DarkSword exact-device boot profile",
            Filter = "DarkSword boot profile (boot-profile.json;*.json)|boot-profile.json;*.json|JSON files (*.json)|*.json",
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) != true) return;

        try
        {
            var profile = await _bootProfileStore.LoadAsync(dialog.FileName)
                          ?? throw new InvalidDataException("The selected JSON is not a DarkSword boot profile.");
            if (!string.IsNullOrWhiteSpace(DetectedProductType) &&
                !string.Equals(profile.ProductType, DetectedProductType, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    $"The profile targets {profile.ProductType}, but the currently detected device is {DetectedProductType}.");
            }

            profile = await RelocateBootProfileAsync(profile, dialog.FileName);
            var resources = Path.Combine(_tools.Root, "resources");
            var validation = await _bootProfileStore.ValidateAssetsAsync(
                profile,
                Path.Combine(resources, "sep_racer.bin"),
                Path.Combine(resources, "kpf.bin"));
            if (!validation.IsValid) throw new InvalidDataException(validation.Summary);

            _activeBootProfile = validation.Profile;
            _bootAssetValidated = true;
            PtePathBox.Text = profile.PtePath;
            await _bootProfileStore.SaveAsync(profile);
            AppendLog(
                $"Imported portable exact-device profile {profile.Key}. Resolved session={profile.SessionDirectory}, PTE={profile.PtePath}.");
            RefreshBootProfileStatus();
            RefreshEnhancedActionState();
        }
        catch (Exception exception)
        {
            _activeBootProfile = null;
            _bootAssetValidated = false;
            SetBootButtonEnabled(false);
            AppendLog($"Portable boot-profile import failed: {exception}");
            ShowMessage(exception.Message, "Boot profile import failed", MessageBoxImage.Error);
        }
    }

    private static async Task<DarkSwordBootProfile> RelocateBootProfileAsync(
        DarkSwordBootProfile profile,
        string selectedProfilePath)
    {
        var profileDirectory = Path.GetDirectoryName(Path.GetFullPath(selectedProfilePath))
                               ?? throw new InvalidDataException("The selected profile has no parent directory.");
        if (File.Exists(profile.PtePath))
            return profile with { SessionDirectory = Path.GetFullPath(profile.SessionDirectory), PtePath = Path.GetFullPath(profile.PtePath) };

        var originalName = Path.GetFileName(profile.PtePath);
        var candidates = new List<string>();
        if (!string.IsNullOrWhiteSpace(originalName))
            candidates.Add(Path.Combine(profileDirectory, originalName));
        candidates.AddRange(Directory.EnumerateFiles(profileDirectory, "*", SearchOption.AllDirectories)
            .Where(path => Path.GetFileName(path).Contains("pte", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith(".metadata.json", StringComparison.OrdinalIgnoreCase))
            .Take(100));

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate)) continue;
            await using var stream = File.OpenRead(candidate);
            var hash = Convert.ToHexString(await SHA256.HashDataAsync(stream)).ToLowerInvariant();
            if (!string.Equals(hash, profile.PteSha256, StringComparison.OrdinalIgnoreCase)) continue;
            var metadataPath = candidate + ".metadata.json";
            if (!File.Exists(metadataPath))
                throw new InvalidDataException($"The matching PTE was found, but its metadata file is missing: {metadataPath}");
            return profile with
            {
                SessionDirectory = profileDirectory,
                PtePath = Path.GetFullPath(candidate),
                UpdatedAt = DateTimeOffset.UtcNow,
            };
        }

        throw new FileNotFoundException(
            "The PTE referenced by boot-profile.json was not found. Keep the complete session folder together; the importer searched the copied folder by the saved PTE SHA-256.",
            profile.PtePath);
    }

    private void DisposePortableBootProfileOverride()
    {
        if (!_portableBootProfileOverrideInitialized || _bootProfileBrowseButton is null) return;
        _portableBootProfileOverrideInitialized = false;
        _bootProfileBrowseButton.Click -= ImportPortableBootProfile_Click;
    }
}
