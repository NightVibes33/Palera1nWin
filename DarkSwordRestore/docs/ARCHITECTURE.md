# Architecture

```text
DarkSwordRestore.exe (WPF, .NET 8)
    |
    +-- IpswInspector
    +-- AppleUsbMonitor
    +-- DfuDriverService
    +-- RestoreOrchestrator
    |       |
    |       +-- gaster.exe                 A9 pwned DFU
    |       +-- turdus_merula.exe          SHC/PTE and firmware restore
    |       +-- openra1n.exe               checkm8 + PongoOS boot
    |
    +-- PongoTransport (libusb P/Invoke)
            |
            +-- sep_racer.bin
            +-- device PTE block
            +-- kpf.bin
            +-- bootux
```

## Isolation and checkpoints

Native tools execute as child processes with redirected logs, cancellation and timeouts. The UI process never edits an IPSW. Each destructive stage writes `session.json` under a unique portable session folder.

The Pongo stage remains in-process because the transport is a small, explicit libusb protocol and progress must be streamed to the desktop interface. No kernel driver is authored by this repository; libwdi assigns the existing signed libusbK driver only to Apple DFU mode.

## Trust model

- Input firmware is inspected before restore.
- Only `iPad6,11` and `iPad6,12` are accepted by the supported flow.
- Generated SHC/PTE files remain local to the user's session directory.
- ECIDs are not intentionally uploaded.
- GitHub Actions produces SHA-256 manifests for the portable package.
- Hardware completion is not claimed until all gates in `HARDWARE_VALIDATION.md` pass on a physical device.
