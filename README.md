# Palera1nWin + DarkSword Restore

Experimental Windows application that combines a **palera1n jailbreak workflow** with the **DarkSword iOS/iPadOS 15 tethered-downgrade workflow** in one WPF interface.

The packaged application is named **DarkSword Restore**, while the main executable remains `Palera1nWin.exe`.

> [!WARNING]
> This project is under active development. The current source builds, tests, packages, and passes deterministic runtime smoke tests in GitHub Actions, but a green CI build does **not** prove that a physical device can complete checkm8, boot PongoOS, jailbreak, restore firmware, or tether-boot successfully.
>
> The current loader replacement still requires confirmation on the primary test device: **iPad 5th generation Wi-Fi (`iPad6,11`, A9) running iPadOS 16.7.11**.

> [!CAUTION]
> **Downgrade erases the device.** Back up important data, save authenticator recovery codes, and know the Apple ID password associated with Activation Lock before approving an erase.

This is not an official palera1n, Apple, openra1n, libimobiledevice, usbipd-win, or turdus project.

---

## Current development status

| Area | Current status |
|---|---|
| Windows WPF application | Builds and launches in CI |
| Managed tests | Passing |
| Windows PowerShell launcher | Parsed and executed by packaged smoke tests |
| Native Windows toolchain | Built from pinned source revisions |
| Portable ZIP packaging | Passing manifest and SHA-256 verification |
| Jailbreak on a physical device | **Not yet confirmed with the current build** |
| PongoOS handoff on `iPad6,11` | **Requires a new physical test** |
| Complete iOS 15 downgrade | **Not yet physically confirmed** |
| Post-downgrade tethered cold boot | **Not yet physically confirmed** |

Earlier physical testing exposed two real defects:

1. The packaged `palera1n.ps1` file could fail Windows PowerShell parsing before palera1n started.
2. The old production Pongo route mixed openra1n's legacy boot shellcode with a different palera1n Pongo image, allowing checkm8 and payload transfer to finish while the device returned to normal mode.

The current build replaces that production path with official palera1n's internally matched checkra1n/Pongo pair and adds an actual Windows PowerShell parser regression. Physical validation is still required.

---

## Main workflows

### Jailbreak

Use **Jailbreak** to keep the currently installed firmware and run palera1n.

Current flow:

1. Validate the packaged runtime, WSL installation, USB state, and connected Apple device.
2. Temporarily stop Apple Mobile Device Service when required.
3. Guide the user into DFU with the synchronized device-bezel animation.
4. Assign the required DFU USB driver and transfer the selected Apple USB device through `usbipd-win`.
5. Start official palera1n's matched checkra1n/Pongo loader in WSL.
6. Detect PongoOS as Apple USB `05AC:4141`.
7. Continue with palera1n's jailbreak payload flow.
8. Clean up USB ownership and restore services.

Rootless is the recommended option. A full device reboot removes the active jailbreak state, so the Jailbreak workflow must be run again.

### DarkSword downgrade

Use **Downgrade** to erase the device and install a supported iOS/iPadOS 15 IPSW through the DarkSword restore backend.

The visible Downgrade screen has four primary actions:

| Action | Purpose |
|---|---|
| **Start Downgrade** | Select and validate an iOS/iPadOS 15 IPSW, bind the session to the detected ProductType and ECID, approve the erase, and run the restore workflow. |
| **Test DFU → Pwned/Pongo** | Non-destructive hardware test for DFU detection, USB driver state, checkm8, PongoOS enumeration, and the Pongo bridge. |
| **Boot Device** | Tether-boot an already downgraded device using its saved `boot-profile.json`. Required after a shutdown, restart, or dead battery. |
| **Import Boot Profile** | Load a completed DarkSword `boot-profile.json` when automatic discovery does not find it. |

The active IPSW verifier accepts **iOS/iPadOS 15.x only**. It checks:

- ZIP structure and safe archive paths
- `BuildManifest.plist` and `Restore.plist`
- Product version and build version
- Supported ProductType values
- iBSS, iBEC, and SEP firmware presence
- Full IPSW SHA-256
- Exact connected ProductType and ECID

The current Windows SEP-block restore backend is enabled only for supported **A9-class DarkSword catalog targets**. The primary target is the iPad 5th generation (`iPad6,11` / `iPad6,12`). Broader jailbreak support does not mean broader downgrade support.

A successful downgrade session is designed to preserve device-bound artifacts including:

- `boot-profile.json`
- SHC block data
- PTE block data
- Restore-session metadata
- IPSW identity and hash information

Keep the complete session directory. Do not edit the ECID, ProductType, file paths, or hashes to bypass validation.

### Tethered cold boot

A DarkSword-downgraded installation is designed to be tethered. After any full shutdown, restart, or dead battery:

1. Connect the exact downgraded device.
2. Enter clean DFU.
3. Open **Downgrade**.
4. Import the correct `boot-profile.json` if it was not detected automatically.
5. Press **Boot Device**.

The app rechecks ProductType, ECID, profile integrity, PTE data, and required resources before sending the boot sequence.

---

## Guided DFU interface

Both workflows use the same synchronized DFU guide.

The guide includes:

- A device bezel overlay with highlighted physical buttons
- Device-specific button labels
- A three-second preparation phase
- An eight-second dual-button hold phase
- A ten-second second-button hold phase
- Stopwatch-based timing instead of accumulating one-second UI delays
- A large countdown, progress indicator, and current-step instructions
- Immediate completion when real DFU or PongoOS is detected
- A warning that a correct DFU screen remains completely black
- Cancel and retry handling

For the iPad 5th generation, the guide uses the **Top + Home** sequence.

---

## Requirements

### Host computer

- Windows 11 x64
- Administrator access
- WSL2
- Ubuntu or another supported Debian/Ubuntu WSL distribution
- `usbipd-win`
- Apple Mobile Device drivers from Apple Devices or iTunes
- Windows PowerShell 5.1
- A reliable direct USB data connection
- Internet access for setup and firmware metadata
- At least 20 GB free for downgrade work; larger IPSWs may require more

The downgrade preflight calculates required storage as the greater of approximately **20 GB** or **2.5× the IPSW size plus 5 GB**.

### Cable and USB guidance

- Prefer a direct motherboard USB port.
- Avoid hubs when troubleshooting DFU or PongoOS enumeration.
- USB-A to Lightning is generally more reliable for checkm8-era devices than some USB-C adapters or hubs.
- Disconnect every other Apple device before starting.
- Close Zadig, gaster, other jailbreak tools, and separate `usbipd` terminals while the app owns the USB transaction.

---

## First-time setup

1. Download and fully extract the current `DarkSword-Restore-win-x64` package.
2. Do not overwrite an older extracted build. Use a new folder.
3. Run `Palera1nWin.exe` as Administrator.
4. Open **Setup**.
5. Confirm WSL2, Ubuntu, `usbipd-win`, the bundled toolchain, and Apple drivers are detected.
6. Press **Provision WSL**.
7. Let the app install the packaged palera1n runtime and `pln-run.sh` under `/opt/palera1n/`.
8. Connect only the target Apple device.

Re-run **Provision WSL** after installing a build that changes the packaged palera1n runtime or launcher scripts.

---

## Recommended test order

For the iPad 5 development target:

1. Extract the latest build into a new folder.
2. Run the app as Administrator.
3. Provision WSL again.
4. Enter clean DFU.
5. Press **Test DFU → Pwned/Pongo** before attempting an erase.
6. Confirm the log contains:

```text
[DarkSword] Starting official palera1n matched checkra1n/PongoOS loader
```

7. Confirm Windows detects PongoOS USB `05AC:4141`.
8. Only after the non-destructive test passes should **Start Downgrade** be considered.

If the log says `Starting Windows checkm8/PongoOS core`, an older build is running.

---

## USB and driver behavior

The app coordinates USB ownership instead of expecting the user to manually switch drivers throughout the workflow.

Depending on device mode it may:

- Stop and later restore Apple Mobile Device Service
- Verify or install `libusbK` for Apple DFU `05AC:1227`
- Accept `libusbK` or WinUSB for PongoOS `05AC:4141`
- Bind, attach, detach, or unbind the exact Apple USB bus through `usbipd-win`
- Reject multiple connected Apple devices
- Reject stale generic pwned-DFU states
- Detect UsbDk conflicts
- Return the selected bus to Windows after WSL operations

Use **Fix Windows Drivers** after testing if Apple Devices or iTunes no longer detects the device normally.

---

## Packaged components

The portable package includes the managed application plus a `toolchain` directory containing the required launchers, binaries, resources, and verification manifests.

Important components include:

| Component | Role |
|---|---|
| `Palera1nWin.exe` | Main WPF application |
| `toolchain/openra1n.exe` | Shared Windows entry point that starts official palera1n's matched Pongo loader in WSL and watches for `05AC:4141` |
| `toolchain/openra1n-core.exe` | Legacy openra1n diagnostic binary; no longer the production Pongo boot path |
| `toolchain/windows/palera1n.ps1` | Windows PowerShell/WSL launcher |
| `toolchain/palera1n.cmd` | Command wrapper for the PowerShell launcher |
| `toolchain/dist/palera1n-linux-x86_64` | Pinned official palera1n Linux runtime |
| `toolchain/turdus_merula.exe` | Restore and SHC/PTE operations |
| `toolchain/darksword-pongo.exe` | PongoOS probe and DarkSword boot commands |
| `toolchain/wdi-simple.exe` | Automated USB-driver installation helper |
| `toolchain/ideviceinfo.exe` | Normal-mode device identity queries |
| `toolchain/irecovery.exe` | Recovery/DFU identity queries |
| `toolchain/resources/sep_racer.bin` | SEP exploit resource used by the tethered restore plan |
| `toolchain/resources/kpf.bin` | Kernel patchfinder resource |
| `toolchain/native-build-manifest.txt` | Pinned native source and runtime metadata |
| `toolchain/native-SHA256SUMS.txt` | Native-stage checksums |
| `manifest.json` | Packaged-file sizes and SHA-256 values |

---

## Logs and support data

Session logs are stored by the application and can be viewed from the Logs interface. A useful failure report should include:

- Complete session log
- App build number or artifact run
- Windows version
- WSL distribution
- Device ProductType
- Installed iOS/iPadOS version and build
- USB cable/port type
- Whether `05AC:1227` and `05AC:4141` appeared
- Whether the device returned to Normal, Recovery, DFU, or PongoOS

Remove Apple IDs, usernames, local paths, ECIDs, serial numbers, and other private identifiers before posting logs publicly. The project also includes redacted support-export services for DarkSword sessions.

---

## Common failures

### PowerShell reports `Missing ')'` or `Unexpected token`

That indicates an obsolete package containing the old broken launcher. Delete the entire extracted directory, extract a current package into a new folder, and provision WSL again.

### Checkm8 succeeds but the device returns to normal mode

This means exploit stages and payload transfer are not enough by themselves. PongoOS must enumerate as `05AC:4141`. Use the latest matched-loader build, a direct USB port, and the non-destructive DFU/Pongo test before attempting a downgrade.

### PongoOS never appears

- Force-reboot the device.
- Re-enter clean DFU.
- Confirm the screen is completely black.
- Use a direct USB port and a known data cable.
- Disconnect other Apple devices.
- Re-run Setup and Provision WSL.
- Verify `usbipd list` does not show a stale attachment.

### Device is stuck in recovery

Recovery mode is not DFU. Use the app's recovery-exit action or force-reboot, then enter DFU again.

### Apple Devices or iTunes cannot see the device

Use **Fix Windows Drivers** to remove the temporary libusbK/WinUSB assignment and restore the stock Apple driver.

### UsbDk conflict

UsbDk can conflict with `usbipd-win`. Remove UsbDk, reboot Windows, and retry.

---

## Building the managed application

Install the .NET 8 and .NET 10 SDKs, then run:

```powershell
git clone https://github.com/NightVibes33/Palera1nWin.git
cd Palera1nWin

dotnet restore src/Palera1nWin.App/Palera1nWin.App.csproj `
  -r win-x64 `
  -p:SelfContained=true

dotnet build src/Palera1nWin.App/Palera1nWin.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -warnaserror

dotnet test tests/Palera1nWin.Core.Tests/Palera1nWin.Core.Tests.csproj `
  -c Release `
  -warnaserror

dotnet test DarkSwordRestore/tests/DarkSwordRestore.Core.Tests/DarkSwordRestore.Core.Tests.csproj `
  -c Release `
  -warnaserror
```

Publish the self-contained Windows application with:

```powershell
dotnet publish src/Palera1nWin.App/Palera1nWin.App.csproj `
  -c Release `
  -r win-x64 `
  --self-contained true `
  -p:PublishSingleFile=false `
  -p:TreatWarningsAsErrors=true `
  -o DarkSwordRestore/build/publish
```

The complete native Windows toolchain is built by `.github/workflows/darksword-restore-windows.yml` using pinned source revisions and MSYS2/MinGW64. The workflow then:

1. Builds and tests the managed application.
2. Builds the native restore toolchain.
3. Verifies required binaries and source pins.
4. Creates the portable package.
5. Runs the packaged-runtime smoke test.
6. Generates manifest and SHA-256 data.
7. Uploads the `DarkSword-Restore-win-x64` artifact.

Tagged public releases additionally require configured Windows Authenticode signing credentials.

---

## Repository layout

| Path | Purpose |
|---|---|
| `src/Palera1nWin.App` | WPF user interface, onboarding, setup, Jailbreak, Downgrade, and DFU guide |
| `src/Palera1nWin.Core` | Jailbreak orchestration, USB monitoring, drivers, WSL, usbipd, process handling, and settings |
| `DarkSwordRestore/src/DarkSwordRestore.Core` | IPSW inspection, exact identity binding, restore sessions, Pongo bridge, SHC/PTE flow, tether boot, and support exports |
| `DarkSwordRestore/native` | Windows native wrappers and Pongo bridge sources |
| `DarkSwordRestore/runtime/jailbreak` | Packaged palera1n launcher, WSL provisioner, and continuation shim |
| `DarkSwordRestore/scripts` | Native build, packaging, patching, and smoke-test scripts |
| `tests/Palera1nWin.Core.Tests` | Jailbreak/core and UI-source regression tests |
| `DarkSwordRestore/tests/DarkSwordRestore.Core.Tests` | Restore-core tests |
| `.github/workflows` | Managed validation and complete Windows artifact builds |

---

## Safety and limitations

- This project cannot make an unsupported device vulnerable to checkm8.
- A successful payload transfer is not the same as a successful PongoOS boot.
- A successful PongoOS boot is not the same as a completed jailbreak or downgrade.
- CI cannot emulate the exact physical Apple USB transition sequence.
- Downgrade is destructive and should not be attempted until the non-destructive Pongo test succeeds.
- The downgraded system is designed to require tethered boot support.
- Do not disconnect the device during restore, SHC/PTE generation, or tether boot.
- Do not bypass ECID, ProductType, IPSW, profile, or checksum validation.
- Keep a current Apple-signed restore path available in case recovery is required.

---

## Credits and licenses

This project integrates or builds on work from:

- [palera1n](https://github.com/palera1n/palera1n)
- [openra1n](https://github.com/mineek/openra1n)
- [libimobiledevice](https://github.com/libimobiledevice)
- [usbipd-win](https://github.com/dorssel/usbipd-win)
- [libusb](https://github.com/libusb/libusb)
- [turdus-merula / idevicerestore work](https://github.com/turdus-m3rula)
- checkm8, PongoOS, and related jailbreak-community research

Third-party components remain under their respective licenses. Review `THIRD_PARTY_NOTICES.md`, the `licenses` directory, and the source repositories before redistribution.

---

## Contributing

Useful contributions include:

- Reproducible physical-device logs
- USB transition traces for `05AC:1227` and `05AC:4141`
- Windows/WSL compatibility fixes
- Driver-state detection improvements
- IPSW validation tests
- Restore-session recovery tests
- Documentation corrections that distinguish CI validation from physical-device validation

Do not report a workflow as working unless the log proves the complete physical stage being claimed.
