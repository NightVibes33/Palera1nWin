# Palera1nWin

Production-grade **Windows GUI** for [palera1n](https://github.com/palera1n/palera1n) — automated hybrid jailbreak (native `openra1n` checkm8 + WSL `palera1n` payloads) with a Fluent / Acrylic shell.

> **Not an official palera1n product.** Official Windows path remains [palen1x](https://docs.palera.in/docs/get-started/installing-palen1x-windows/). This app packages the hybrid flow validated on Windows 11 + WSL2.

---

## Features

- Fluent dark UI (WPF-UI 4.3, Acrylic backdrop, teal accent)
- One-click jailbreak orchestrator: DFU helper → libusbK → openra1n (Pongo 2.6.3) → palera1n payloads
- Device live monitor (Normal / Recovery / DFU / YOLO / Pongo)
- Driver assist for `05AC:1227` / `1281` / `4141` (auto when possible, Zadig fallback)
- **Fix Windows Drivers** — one-click restore of default Apple drivers after jailbreak (removes libusbK/WinUSB so iTunes/Apple Devices can see the phone again)
- **UsbDk uninstall** — one-click removal of the conflicting UsbDk filter driver
- Fetch / select palera1n versions from GitHub Releases
- Settings: rootless/rootful, safe mode, verbose boot (`-V`), toolchain root, WSL distro
- **Portable** — settings, logs, and runtime files live next to the exe (no `AppData` pollution)
- Logs + Setup doctor checks (WSL, usbipd, toolchain, UsbDk conflict detection, WSL runtime)
- **Bundled toolchain** — `openra1n.exe`, `palera1n.ps1` launcher, `palera1n-linux-x86_64` binary, `wdi-simple.exe`, `zadig.exe`, `gaster.exe`, and provision scripts ship in `toolchain\` next to the exe. No separate toolchain download required.
- **One-click Provision WSL** — installs the `palera1n` runtime + `pln-run.sh` wrapper into `/opt/palera1n/` inside your WSL distro from the Setup tab (no manual `setup.ps1`).

---

## Supported devices

Palera1nWin follows [palera1n's own device support](https://docs.palera.in/docs/reference/compatibility-chart/) exactly — it's per-model, not just per-chip, so not every device sharing a supported SoC is necessarily included (e.g. iPhone 6/6 Plus are A8 but are **not** on palera1n's supported list).

| Chip | Devices | Status |
|------|---------|--------|
| **A11** | iPhone 8, iPhone 8 Plus, iPhone X | Supported (passcode must stay disabled) |
| **A10 / A10X** | iPhone 7, iPhone 7 Plus, iPad (6th/7th gen), iPad Pro 10.5", iPad Pro 12.9" (2nd gen), iPod touch (7th gen), Apple TV 4K (1st gen) | Supported |
| **A9 / A9X** | iPhone 6s, iPhone 6s Plus, iPhone SE (2016), iPad (5th gen), iPad Pro 9.7", iPad Pro 12.9" (1st gen) | Supported |
| **A8 / A8X** | iPad mini 4, iPad Air 2, Apple TV HD | Supported |
| A7 and earlier | iPhone 5s, iPad Air (1st gen), iPad mini 2/3, etc. | **Not supported** by palera1n |
| A12 and newer | iPhone XS and newer | **Not supported** (checkm8 is A8–A11 only) |

Apple T2-equipped Macs are also jailbreakable by palera1n, but use a separate restore-mode flow this GUI does not cover.

> **A11 note:** On iPhone 8 / 8 Plus / X you **must** disable passcode & Touch ID before jailbreaking. On iOS 16+, you need to reset the device (and keep the passcode disabled) before proceeding — see [palera1n's compatibility chart](https://docs.palera.in/docs/reference/compatibility-chart/) for details.

---

## Quick start (end users)

### 1. Install prerequisites

| Component | Why | Install |
|-----------|-----|---------|
| **WSL2 + Ubuntu** | Runs `palera1n` Linux payloads | `wsl --install -d Ubuntu` in an **admin** PowerShell, then reboot |
| **usbipd-win** | Bridges the iPhone's USB to WSL | Download from [github.com/dorssel/usbipd-win/releases](https://github.com/dorssel/usbipd-win/releases) and install |
| **Apple Mobile Device driver** | iTunes-style recovery/normal mode | Bundled with iTunes / Apple Devices (Microsoft Store) |
| **Visual C++ Redistributable** | `openra1n.exe` runtime | [vc_redist.x64.exe](https://aka.ms/vs/17/release/vc_redist.x64.exe) |

### 2. Download Palera1nWin

Grab the latest `Palera1nWin-win-x64.zip` from [Releases](../../releases). Unzip to any folder. **Run as Administrator** (right-click → Run as administrator) — this is required for driver installation and `usbipd` detach/attach.

The release zip is self-contained: the `Palera1nWin.exe` and a `toolchain\` folder (with `openra1n.exe`, the WSL `palera1n` binary, launcher scripts, and driver tooling) ship together. No extra downloads.

### 3. First-run setup

1. Open the **Setup** tab. The app checks WSL, usbipd, the bundled toolchain, and the WSL `palera1n` runtime automatically.
2. Click **Provision WSL** (one-time). This installs the `palera1n` binary + `pln-run.sh` wrapper into `/opt/palera1n/` inside your WSL distro and pulls the runtime packages (`usbmuxd`, `usbutils`, `libusb`, `usbip`). Accept the UAC prompt if it appears.
3. Connect your iPhone via a **USB-A to Lightning** cable (USB-C adapters are unreliable for DFU).

### 4. Jailbreak

1. Open the **Jailbreak** tab.
2. Click **Start Jailbreak**.
3. When the **"Press Enter when ready for DFU mode"** dialog appears, follow the on-screen button sequence to enter DFU.
4. The app handles the rest: installs `libusbK`, runs `openra1n` (checkm8 + PongoOS upload), bridges the device to WSL, and runs `palera1n` payloads.
5. When you see **"Jailbreak flow completed"**, your device will respring with the jailbreak active.

---

## How it works

```
┌─────────────┐     ┌──────────────┐     ┌─────────────┐     ┌──────────────┐
│  DFU helper  │────▶│  libusbK on   │────▶│   openra1n   │────▶│   PongoOS    │
│ (palera1n -D)│     │  Windows host │     │  (checkm8)   │     │  (05AC:4141) │
└─────────────┘     └──────────────┘     └─────────────┘     └──────┬───────┘
                                                                        │
                                                                        ▼
┌─────────────┐     ┌──────────────┐     ┌─────────────────────────────────┐
│  Device on  │◀────│  usbipd      │◀────│  palera1n (WSL)                  │
│  PongoOS    │     │  attach to   │     │  Pongo payloads + rootless/fs   │
│  libusbK    │     │  WSL         │     │  jailbreak                       │
└─────────────┘     └──────────────┘     └─────────────────────────────────┘
```

1. **DFU helper** — `palera1n -D` in WSL guides the device into DFU mode (you do the button presses).
2. **libusbK** — the app silently installs the `libusbK` driver on the DFU device (via `wdi-simple.exe`, bundled). No manual Zadig needed.
3. **openra1n** — Windows-native `openra1n.exe` runs the checkm8 exploit and uploads PongoOS. A background watchdog keeps `libusbK` active.
4. **usbipd bridge** — the PongoOS device is attached to WSL via `usbipd-win`.
5. **palera1n payloads** — `palera1n` in WSL sends the rootless/rootful payloads over PongoOS.
6. **Release** — the device is detached from WSL and returned to the Windows host.

---

## Troubleshooting

### "Device not recognized by iTunes / Apple Devices after jailbreak"
After a jailbreak session, the Apple USB device may still be on the `libusbK` driver (installed for `openra1n`). To restore the default Apple driver:
- Open the **Device** tab → click **Fix Windows Drivers**. This removes `libusbK`/`WinUSB` from all connected Apple devices and triggers a hardware re-scan so Windows re-installs the stock Apple driver. The device will briefly disconnect and reconnect.

### "openra1n exited with code -1073741819 (ACCESS_VIOLATION)"
The DFU device was on the wrong driver (`VBoxUSB`/`WinUSB` instead of `libusbK`). The app auto-fixes this, but if it persists:
- Close the app, open Zadig (bundled in `toolchain\dist\native\zadig.exe`), select `Apple Mobile USB (DFU)` → replace driver with `libusbK` → retry.

### "PongoOS USB device never appeared"
`openra1n` ran but PongoOS didn't enumerate. Usually a stale YOLO state:
- Force-restart the iPhone (Volume Up → Volume Down → hold Side until Apple logo).
- Re-enter DFU mode and click **Start Jailbreak** again.

### "Whoops, device did not enter DFU mode"
The DFU button timing was off. The app continues if the device is actually in DFU, but if it genuinely failed:
- Try again — DFU entry is timing-sensitive. Use a USB-A cable if possible.

### "UsbDk filter is installed"
UsbDk conflicts with `usbipd-win`. Uninstall it:
- Settings → Apps → search "UsbDk" → Uninstall. Or use the app's **Setup** tab (offers one-click uninstall).

### Device stuck in recovery mode (iTunes logo)
- The app's **Device** tab has a "Exit Recovery" action. Or run `idevicerestore -e` if available.

### "Waiting for devices" hangs (Shared but not Attached)
Caused by UsbDk or stale `usbipd` state. The app kills leftover bridges and uses `bind --force`, but if it persists:
- Close the app, run `usbipd list` in admin PowerShell, then `usbipd unbind --all` and retry.

### Driver keeps flipping back to WinUSB
Windows may race the driver assignment. The global watchdog re-applies `libusbK` automatically. If it keeps failing:
- Disconnect other USB devices, use a direct motherboard USB port (no hub), and run the app as Administrator.

### Logs
Session logs are saved **next to the exe** in `logs\session-YYYYMMDD-HHmmss.log` (portable). Check the **Logs** tab in the app or open the latest file for troubleshooting.

---

## Build (developers)

```powershell
git clone https://github.com/pwnapplehat/Palera1nWin.git
cd Palera1nWin
dotnet build Palera1nWin.slnx -c Release
dotnet test tests\Palera1nWin.Core.Tests -c Release
dotnet run --project src\Palera1nWin.App -c Release
```

Publish a self-contained single-file exe:

```powershell
dotnet publish src\Palera1nWin.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o dist\win-x64
```

The `native\` folder (`wdi-simple.exe`, `zadig.exe`) is copied to the output automatically by the build target.

To build a **release zip** (exe + bundled toolchain), publish to a clean folder then stage the toolchain:

```powershell
dotnet publish src\Palera1nWin.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o publish-v100
# Stage the runtime toolchain (openra1n, scripts, palera1n-linux binary, provision scripts)
# into publish-v100\toolchain\ — see the bundle script in tools\.
Compress-Archive -Path publish-v100\* -DestinationPath Palera1nWin-win-x64.zip -Force
```

### Architecture

| Project | Role |
|---------|------|
| `Palera1nWin.App` | WPF GUI (Fluent / Acrylic, WPF-UI 4.3) |
| `Palera1nWin.Core` | USB monitor, drivers, usbipd/WSL, openra1n, releases API, orchestrator |
| `Palera1nWin.Core.Tests` | Unit tests |

### Key components

| File | Responsibility |
|------|----------------|
| `JailbreakOrchestrator.cs` | End-to-end flow: DFU → libusbK → openra1n → usbipd → palera1n |
| `AppleUsbMonitor.cs` | Live USB device detection (Normal/Recovery/DFU/YOLO/Pongo) |
| `DriverInstaller.cs` | `libusbK` install via `wdi-simple.exe`, driver service detection |
| `LibusbKWatchdog.cs` | Background driver watchdog (re-applies `libusbK` if Windows flips it) |
| `UsbipdService.cs` | `usbipd` list/bind/attach/detach/unbind, Apple device release |
| `OpenRa1nService.cs` | `openra1n.exe` execution, PongoOS detection, stuck/hang detection |
| `Elevation.cs` | UAC elevation for admin-only commands |

---

## Credits

Palera1nWin is a front-end. The actual jailbreak is powered by these projects,
whose binaries it **bundles and redistributes** under their own licenses (see
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) and the `licenses/` folder):

- **[openra1n](https://github.com/mineek/openra1n)** by mineek — Windows checkm8 + PongoOS upload (Apache-2.0). Built from the [wh1te4ever/openra1n](https://github.com/wh1te4ever/openra1n) fork.
- **[gaster](https://github.com/0x7ff/gaster)** by 0x7ff — checkm8 exploit, and the base openra1n builds on (Apache-2.0).
- **[palera1n](https://github.com/palera1n/palera1n)** team — the Linux `palera1n` binary and PongoOS payloads (MIT).
- **checkra1n team / PongoOS** — the checkm8 bootrom exploit and PongoOS pre-boot environment.
- **[ra1npoc](https://github.com/kok3shidoll/ra1npoc)** by kok3shidoll — payloads embedded in openra1n.
- **[libwdi](https://github.com/pbatard/libwdi) / [Zadig](https://zadig.akeo.ie/)** by Pete Batard — automated `libusbK` driver install (LGPL-3.0 / GPL-3.0).
- **[libusb](https://github.com/libusb/libusb)** — USB access for openra1n/gaster (LGPL-2.1).
- **[usbipd-win](https://github.com/dorssel/usbipd-win)** by Frans van Dorsselaer — USB/IP bridging to WSL.
- UI patterns inspired by [BitBroom](https://github.com/pwnapplehat/BitBroom) (WPF-UI Acrylic).

## License

Palera1nWin's own code is **MIT** — see [LICENSE](LICENSE).

Bundled third-party binaries remain under their respective licenses (Apache-2.0,
MIT, LGPL/GPL). Full texts are in [`licenses/`](licenses/) and the release archive.
See [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) for the complete attribution.
