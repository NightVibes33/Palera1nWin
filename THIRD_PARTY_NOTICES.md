# Third-Party Notices

Palera1nWin itself is licensed under the MIT License (see `LICENSE`).

Palera1nWin bundles and **redistributes** the following third-party components in
its release archive (`Palera1nWin-win-x64.zip`, under `toolchain\`). Each is the
property of its respective authors and is distributed here under its own license.
Full license texts are in the `licenses/` folder (shipped alongside the binaries
in the release archive).

| Component | File(s) in bundle | Author / Origin | License |
|-----------|-------------------|-----------------|---------|
| **openra1n** | `dist\openra1n-win\openra1n.exe` | [mineek/openra1n](https://github.com/mineek/openra1n) (built from the [wh1te4ever/openra1n](https://github.com/wh1te4ever/openra1n) fork) | Apache-2.0 |
| **gaster** | `dist\native\gaster.exe` | [0x7ff/gaster](https://github.com/0x7ff/gaster) | Apache-2.0 |
| **palera1n** | `dist\palera1n-linux-x86_64` | [palera1n/palera1n](https://github.com/palera1n/palera1n) | MIT |
| **libwdi / wdi-simple** | `dist\native\wdi-simple.exe` | [pbatard/libwdi](https://github.com/pbatard/libwdi) | LGPL-3.0 |
| **Zadig** | `dist\native\zadig.exe` | [pbatard/libwdi (Zadig)](https://github.com/pbatard/libwdi) | GPL-3.0 |
| **libusb** | `dist\openra1n-win\libusb-1.0.dll`, `dist\native\libusb-1.0.dll` | [libusb/libusb](https://github.com/libusb/libusb) | LGPL-2.1 |

> **Modification notice (Apache-2.0 §4b):** the bundled `openra1n.exe` is a
> **modified build** of openra1n — `openra1n.c` is patched for Windows/libusbK
> reliability and the embedded `Pongo.bin` is swapped to palera1n's PongoOS
> 2.6.3. All original copyright and attribution notices are retained.

---

## openra1n — Apache License 2.0

```
Copyright 2023 Mineek
Some code from gaster - Copyright 2023 0x7ff
```

openra1n further embeds payloads/attributions from the palera1n team, the
checkra1n team / PongoOS, and [kok3shidoll/ra1npoc](https://github.com/kok3shidoll/ra1npoc).
Licensed under the Apache License, Version 2.0. See `licenses/Apache-2.0.txt`.

## gaster — Apache License 2.0

```
Copyright 2023 0x7ff
```

Licensed under the Apache License, Version 2.0. See `licenses/Apache-2.0.txt`.

## palera1n — MIT License

```
Copyright 2023 palera1n team
```

See `licenses/palera1n-LICENSE.txt`.

## libwdi (wdi-simple.exe) — LGPL-3.0 / Zadig (zadig.exe) — GPL-3.0

Copyright © Pete Batard and libwdi contributors. `wdi-simple.exe` and `zadig.exe`
are redistributed **unmodified** from the libwdi/Zadig project.
See `licenses/libwdi-COPYING-LGPLv3.txt` and `licenses/libwdi-COPYING-GPLv3.txt`.
Corresponding source: <https://github.com/pbatard/libwdi>.

## libusb (libusb-1.0.dll) — LGPL-2.1

Copyright © the libusb contributors. Redistributed unmodified.
License and source: <https://github.com/libusb/libusb/blob/master/COPYING>.

---

*If you are an author of any component above and want attribution changed or a
component removed, please open an issue on the Palera1nWin repository.*
