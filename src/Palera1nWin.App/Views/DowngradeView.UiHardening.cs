using System.IO;
using System.Text;
using System.Windows.Controls;

namespace Palera1nWin.App.Views;

public partial class DowngradeView
{
    private const int MaximumLogBoxCharacters = 300_000;
    private const int RetainedLogBoxCharacters = 220_000;
    private const long MaximumDiskLogBytes = 24L * 1024L * 1024L;
    private const long RetainedDiskLogBytes = 12L * 1024L * 1024L;
    private bool _uiHardeningInitialized;
    private bool _trimmingLogBox;
    private DateTimeOffset _lastDiskLogTrim = DateTimeOffset.MinValue;

    private void InitializeUiHardening()
    {
        if (_uiHardeningInitialized) return;
        _uiHardeningInitialized = true;
        LogBox.TextChanged += BoundedLogBox_TextChanged;
        InitializePortableBootProfileOverride();
    }

    private void BoundedLogBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_trimmingLogBox) return;
        try
        {
            if (LogBox.Text.Length > MaximumLogBoxCharacters)
            {
                _trimmingLogBox = true;
                LogBox.Text = "[Earlier UI log lines omitted]\n" + LogBox.Text[^RetainedLogBoxCharacters..];
                LogBox.CaretIndex = LogBox.Text.Length;
                LogBox.ScrollToEnd();
            }
            if (DateTimeOffset.UtcNow - _lastDiskLogTrim > TimeSpan.FromMinutes(1))
            {
                _lastDiskLogTrim = DateTimeOffset.UtcNow;
                TrimDiskLogBestEffort();
            }
        }
        finally
        {
            _trimmingLogBox = false;
        }
    }

    private void TrimDiskLogBestEffort()
    {
        try
        {
            lock (_logLock)
            {
                if (!File.Exists(_logPath)) return;
                var info = new FileInfo(_logPath);
                if (info.Length <= MaximumDiskLogBytes) return;
                using var input = new FileStream(_logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                input.Seek(Math.Max(0, input.Length - RetainedDiskLogBytes), SeekOrigin.Begin);
                using var memory = new MemoryStream();
                input.CopyTo(memory);
                var temporary = _logPath + ".trim";
                File.WriteAllText(temporary, "[Earlier disk log lines omitted]" + Environment.NewLine, new UTF8Encoding(false));
                using (var output = new FileStream(temporary, FileMode.Append, FileAccess.Write, FileShare.None))
                    memory.WriteTo(output);
                File.Move(temporary, _logPath, overwrite: true);
            }
        }
        catch
        {
            // Log retention must never affect USB, restore, or boot execution.
        }
    }

    private void DisposeUiHardening()
    {
        if (!_uiHardeningInitialized) return;
        _uiHardeningInitialized = false;
        LogBox.TextChanged -= BoundedLogBox_TextChanged;
        DisposePortableBootProfileOverride();
        DisposeSafeExportOverrides();
        DisposeExactIdentityGuard();
    }
}
