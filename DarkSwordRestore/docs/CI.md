# Continuous integration

The DarkSword workflow builds on a clean Windows 2025 runner and produces one artifact named `DarkSword-Restore-win-x64` containing the portable ZIP and `SHA256SUMS.txt`.

The build is considered successful only after:

1. All native dependencies compile under MinGW64.
2. The turdus-enabled restore executable links successfully.
3. The WPF application builds with warnings treated as errors.
4. IPSW and session self-tests pass.
5. The complete package is created and uploaded.

A green hosted run is a software-build gate, not proof that physical DFU/restore stages passed.
