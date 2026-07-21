# Third-Party Notices

DarkSword Restore orchestrates and packages components from independent upstream projects. Each component remains governed by its own license. This file is informational and does not replace the upstream license text.

## turdus merula idevicerestore fork

- Project: `turdus-m3rula/idevicerestore_fork`
- Branch used by the build: `sephaxx`
- Role: firmware restore, SHC/PTE operations and embedded Pongo modules
- Base project license: GNU Lesser General Public License 2.1 or later, with additional files retaining their own notices

The release build records the exact upstream commit in `native-build-manifest.txt`.

## openra1n

- Project: `mineek/openra1n`
- Role: Windows/libusb checkm8 and PongoOS boot stage
- License: retain the license and copyright notices shipped by the upstream repository

## libimobiledevice projects

The native restore build links components from:

- `libplist`
- `libimobiledevice-glue`
- `libusbmuxd`
- `libimobiledevice`
- `libirecovery`
- `libtatsu`

Each component is distributed according to the license stated by its upstream project. Binary redistribution must include the corresponding license files in a tagged public release.

## libfragmentzip

- Project: `turdus-m3rula/libfragmentzip`
- Branch used by the build: `non-libgeneral`
- Role: partial retrieval of currently signed firmware components
- License: retain the upstream license and notices

## libusb

- Project: `libusb/libusb`
- Role: Windows USB control and bulk transport
- License: GNU Lesser General Public License 2.1 or later

## libwdi / wdi-simple

- Project: `pbatard/libwdi`
- Role: assigning a signed libusbK driver package to Apple DFU mode only
- License: GNU Lesser General Public License 3.0 or later for libwdi/wdi-simple components

## gaster

- Project: `0x7ff/gaster`
- Role: Windows A9 pwned-DFU preparation
- License: retain the license and notices shipped by the upstream project

## Apple firmware

DarkSword Restore does not bundle Apple IPSW files. Users must obtain the correct official firmware archive themselves. Apple names, device identifiers and firmware component names are used solely for interoperability and device identification.

## Release requirement

Before creating a public tagged release, the packaging workflow must include a `licenses` directory containing the exact license texts for every redistributed executable and DLL. The build manifest and SHA-256 file list must remain in the release package.
