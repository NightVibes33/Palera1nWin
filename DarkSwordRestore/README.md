# DarkSword Restore

DarkSword Restore is a native Windows x64 desktop application for performing and maintaining a tethered, blobless downgrade on the Apple A9 iPad (5th generation):

- `iPad6,11` — Wi-Fi
- `iPad6,12` — Wi-Fi + Cellular

The application accepts an original Apple IPSW for the target device even when Apple no longer signs that version. It does not remove Activation Lock and it does not treat arbitrary modified IPSWs as trusted firmware.

## What the app contains

- A WPF desktop interface with live Normal, Recovery, DFU and PongoOS detection.
- Strict IPSW inspection before any destructive operation.
- Targeted libusbK setup for Apple DFU mode (`05AC:1227`).
- Native Windows checkm8/PongoOS stages using `gaster` and `openra1n`.
- The turdus-enabled `idevicerestore` fork compiled for Windows through MSYS2.
- SHC/PTE session storage and checkpoint recovery.
- A native Pongo USB transport that uploads the SEP exploit, the device-specific PTE block and the tethered kernel patchfinder without a Pico board.
- A separate one-click tether boot workflow for every future cold boot.
- Portable logs, diagnostics, session metadata, SHA-256 manifests and release packaging.

## Restore sequence

DarkSword guides the user through the complete A9 sequence:

1. Enter DFU and create the pre-restore SHC block.
2. Enter DFU again and erase/restore the selected official IPSW.
3. Enter DFU after the restore and create the post-restore SHC block.
4. Enter DFU and create the device-specific PTE block.
5. Enter DFU one final time, boot PongoOS, load the SEP/KPF modules and boot iOS.

The resulting installation is tethered. Any restart, shutdown or fully depleted battery requires the **Tether Boot** page and the saved PTE block.

## Build

The GitHub Actions workflow at `.github/workflows/darksword-windows.yml` performs the complete build on `windows-2025`:

1. Installs MSYS2 MinGW64 dependencies.
2. Builds `libtatsu` and the turdus `libfragmentzip` fork.
3. Builds the turdus `idevicerestore_fork` with `--with-turdusmerula=yes`.
4. Builds Windows `openra1n` through its libusb backend.
5. Extracts the embedded turdus Pongo modules to runtime `.bin` resources.
6. Builds the .NET 8 WPF application with warnings treated as errors.
7. Runs dependency-free IPSW/session self-tests.
8. Creates a self-contained Windows x64 ZIP and SHA-256 manifest.

Local managed build:

```powershell
dotnet restore DarkSwordRestore.sln
dotnet build DarkSwordRestore.sln -c Release
dotnet run --project tests\DarkSwordRestore.SelfTest -c Release
```

The native build script is intended for the MSYS2 MinGW64 environment:

```bash
bash scripts/build-native.sh
```

## Required usage conditions

- Windows 11 x64.
- Run as Administrator for the DFU driver assignment.
- Apple Devices or desktop iTunes installed for Apple's normal/recovery drivers.
- Direct USB-A to Lightning is strongly preferred.
- A complete backup before restoring.
- The correct Apple ID credentials if the device requires normal Apple activation.

## Validation status

The repository's hosted workflow can compile, self-test and package the complete Windows application. GitHub-hosted runners cannot physically attach an iPad, so actual DFU timing, SHC/PTE creation and the first tether boot must be validated on a real `iPad6,11` or `iPad6,12`. Failures are logged by stage and the session folder preserves generated artifacts for diagnosis.

## Safety boundaries

DarkSword Restore does not:

- Bypass Activation Lock.
- Remove ownership checks.
- Convert arbitrary custom firmware into trusted Apple firmware.
- Promise an untethered boot without compatible saved signing material.

## Source layout

```text
src/DarkSwordRestore.App/       WPF desktop interface
src/DarkSwordRestore.Core/      Restore, USB, firmware and session engine
tests/DarkSwordRestore.SelfTest Dependency-free build tests
scripts/build-native.sh         MSYS2 native dependency/tool build
scripts/package-release.ps1     Portable ZIP and hashes
tools/extract_resource_arrays.py Embedded module extractor
```

See `THIRD_PARTY_NOTICES.md` for upstream projects and license obligations.
