# Generated toolchain

This directory is populated by the GitHub Actions native build and is intentionally not a source for Apple firmware archives.

Expected packaged layout:

```text
toolchain/
├── native/
│   ├── gaster.exe
│   ├── openra1n.exe
│   ├── turdus_merula.exe
│   ├── irecovery.exe
│   ├── wdi-simple.exe
│   ├── libusb-1.0.dll
│   ├── dependent MinGW DLLs
│   └── native-build-manifest.txt
└── resources/
    ├── sep_racer.bin
    ├── kpf.bin
    ├── cpf.bin
    ├── overlay.bin
    └── union.bin
```

`sep_racer.bin` and the patchfinder resources are extracted at build time from the source arrays in the turdus idevicerestore fork. Do not commit generated binaries to the source branch.
