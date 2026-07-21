using System.Runtime.InteropServices;
using System.Text;

namespace DarkSwordRestore.Core;

public sealed class PongoTransport : IDisposable
{
    private const ushort AppleVendorId = 0x05AC;
    private const ushort PongoProductId = 0x4141;
    private const byte BulkOutEndpoint = 0x02;
    private const int InterfaceNumber = 0;
    private const int TimeoutMs = 5000;
    private const int ChunkSize = 2048;

    private readonly SessionLogger _log;
    private IntPtr _context;
    private IntPtr _handle;
    private bool _disposed;

    public PongoTransport(ToolchainLocator tools, SessionLogger log)
    {
        _log = log;
        NativeLibrary.Load(tools.LibUsb);
    }

    public void Open()
    {
        ThrowIfDisposed();
        if (_handle != IntPtr.Zero) return;

        Check(LibUsb.libusb_init(out _context), "libusb_init");
        _handle = LibUsb.libusb_open_device_with_vid_pid(_context, AppleVendorId, PongoProductId);
        if (_handle == IntPtr.Zero)
        {
            throw new DarkSwordException(RestoreStage.BootingPongo, "PongoOS USB device 05AC:4141 was not found.");
        }

        _ = LibUsb.libusb_set_auto_detach_kernel_driver(_handle, 1);
        Check(LibUsb.libusb_claim_interface(_handle, InterfaceNumber), "libusb_claim_interface");
        _log.Info("Connected to PongoOS over libusb.");
    }

    public async Task TetherBootAsync(
        string sepRacerPath,
        string kpfPath,
        string pteBlockPath,
        IProgress<RestoreProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        ValidateResource(sepRacerPath, "sep_racer");
        ValidateResource(kpfPath, "kpf");
        ValidateResource(pteBlockPath, "pteblock");

        await Task.Delay(750, cancellationToken).ConfigureAwait(false);
        DrainOutput();

        progress?.Report(new RestoreProgress(RestoreStage.LoadingSepExploit, 20, "Loading SEP exploit", "Uploading sep_racer.bin"));
        SendBulk(await File.ReadAllBytesAsync(sepRacerPath, cancellationToken).ConfigureAwait(false), cancellationToken);
        SendCommand("modload\n", cancellationToken);

        progress?.Report(new RestoreProgress(RestoreStage.LoadingSepExploit, 45, "Preparing SEP ticket", "Uploading the device-specific PTE block"));
        SendBulk(await File.ReadAllBytesAsync(pteBlockPath, cancellationToken).ConfigureAwait(false), cancellationToken);
        SendCommand("sep pte\n", cancellationToken);
        SendCommand("sep pwn_pte\n", cancellationToken);
        await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        DrainOutput();

        progress?.Report(new RestoreProgress(RestoreStage.LoadingKernelPatchfinder, 70, "Loading kernel patchfinder", "Uploading kpf.bin"));
        SendBulk(await File.ReadAllBytesAsync(kpfPath, cancellationToken).ConfigureAwait(false), cancellationToken);
        SendCommand("modload\n", cancellationToken);
        SendCommand("kpf-tethered\n", cancellationToken);

        progress?.Report(new RestoreProgress(RestoreStage.BootingXnu, 92, "Booting iOS", "Starting the downgraded kernel"));
        SendCommand("bootux\n", cancellationToken);
        _log.Info("Pongo tether-boot sequence completed.");
    }

    public string ReadConsoleOutput()
    {
        EnsureOpen();
        var builder = new StringBuilder();
        for (var index = 0; index < 128; index++)
        {
            var inProgress = new byte[1];
            var status = ControlTransfer(0xA1, 2, 0, 0, inProgress);
            if (status < 0 || inProgress[0] == 0) break;

            var buffer = new byte[256];
            var read = ControlTransfer(0xA1, 1, 0, 0, buffer);
            if (read <= 0) break;
            builder.Append(Encoding.UTF8.GetString(buffer, 0, read).TrimEnd('\0'));
        }
        var output = builder.ToString();
        if (!string.IsNullOrWhiteSpace(output)) _log.Info($"Pongo: {output}");
        return output;
    }

    private void SendBulk(byte[] data, CancellationToken cancellationToken)
    {
        Clear();
        var size = BitConverter.GetBytes(data.Length);
        CheckTransfer(ControlTransfer(0x21, 1, 0, 0, size), "Pongo bulk setup");

        var offset = 0;
        while (offset < data.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = Math.Min(ChunkSize, data.Length - offset);
            var chunk = new byte[count];
            Buffer.BlockCopy(data, offset, chunk, 0, count);
            var result = LibUsb.libusb_bulk_transfer(_handle, BulkOutEndpoint, chunk, count, out var transferred, TimeoutMs);
            Check(result, $"Pongo bulk transfer at offset {offset}");
            if (transferred != count)
            {
                throw new IOException($"Pongo accepted {transferred} of {count} bytes at offset {offset}.");
            }
            offset += transferred;
        }
        _log.Info($"Pongo resource upload completed: {data.Length:N0} bytes.");
    }

    private void SendCommand(string command, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Clear();
        var data = Encoding.UTF8.GetBytes(command);
        CheckTransfer(ControlTransfer(0x21, 3, 0, 0, data), $"Pongo command {command.Trim()}");

        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var state = new byte[1];
            var result = ControlTransfer(0xA1, 2, 0, 0, state);
            if (result >= 0 && state[0] == 0)
            {
                _log.Info($"Pongo command completed: {command.Trim()}");
                return;
            }
            Thread.Sleep(10);
        }
        throw new TimeoutException($"Pongo command timed out: {command.Trim()}");
    }

    private void Clear()
    {
        var result = ControlTransfer(0x21, 4, 0xFFFF, 0, Array.Empty<byte>());
        CheckTransfer(result, "Pongo clear");
    }

    private void DrainOutput()
    {
        try { _ = ReadConsoleOutput(); }
        catch (Exception ex) { _log.Warn($"Could not drain Pongo output: {ex.Message}"); }
    }

    private int ControlTransfer(byte requestType, byte request, ushort value, ushort index, byte[] data) =>
        LibUsb.libusb_control_transfer(_handle, requestType, request, value, index, data, (ushort)data.Length, TimeoutMs);

    private void EnsureOpen()
    {
        if (_handle == IntPtr.Zero) Open();
    }

    private static void ValidateResource(string path, string name)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"Required {name} resource is missing.", path);
        if (new FileInfo(path).Length == 0) throw new InvalidDataException($"Required {name} resource is empty: {path}");
    }

    private static void CheckTransfer(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation} failed with libusb error {result}.");
    }

    private static void Check(int result, string operation)
    {
        if (result < 0) throw new IOException($"{operation} failed with libusb error {result}.");
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handle != IntPtr.Zero)
        {
            _ = LibUsb.libusb_release_interface(_handle, InterfaceNumber);
            LibUsb.libusb_close(_handle);
            _handle = IntPtr.Zero;
        }
        if (_context != IntPtr.Zero)
        {
            LibUsb.libusb_exit(_context);
            _context = IntPtr.Zero;
        }
    }

    private static class LibUsb
    {
        private const string Library = "libusb-1.0.dll";

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int libusb_init(out IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void libusb_exit(IntPtr context);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern IntPtr libusb_open_device_with_vid_pid(IntPtr context, ushort vendorId, ushort productId);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern void libusb_close(IntPtr handle);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int libusb_claim_interface(IntPtr handle, int interfaceNumber);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int libusb_release_interface(IntPtr handle, int interfaceNumber);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int libusb_set_auto_detach_kernel_driver(IntPtr handle, int enable);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int libusb_control_transfer(
            IntPtr handle,
            byte requestType,
            byte request,
            ushort value,
            ushort index,
            [In, Out] byte[] data,
            ushort length,
            uint timeout);

        [DllImport(Library, CallingConvention = CallingConvention.Cdecl)]
        internal static extern int libusb_bulk_transfer(
            IntPtr handle,
            byte endpoint,
            [In] byte[] data,
            int length,
            out int transferred,
            uint timeout);
    }
}
