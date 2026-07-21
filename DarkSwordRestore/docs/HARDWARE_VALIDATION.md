# Hardware validation gates

GitHub-hosted runners validate compilation, static structure, firmware parsing, session persistence and packaging. A physical `iPad6,11` or `iPad6,12` is required for the following gates.

## Gate 1 — Windows DFU transport

- Apple DFU device enumerates as `05AC:1227`.
- The targeted libusbK assignment completes without changing normal/recovery drivers.
- `gaster pwn` produces a pwned A9 DFU state on a direct USB connection.

## Gate 2 — SHC acquisition

- The turdus Windows restore executable opens the pwned device.
- `--get-shcblock` produces a non-empty BSEP SHC block.
- The session checkpoint stores the exact generated path and SHA-256.

## Gate 3 — Restore

- The selected official iPad 5 IPSW restores with `-o --load-shcblock`.
- Device transitions through iBSS, iBEC and restore mode without a permanent USB-driver conflict.
- The application preserves the full restore log after reconnects.

## Gate 4 — Post-restore ticket generation

- A distinct post-restore SHC block is created.
- `--get-pteblock --load-shcblock` creates a valid PTE block.
- The PTE filename and session metadata remain tied to the device ECID.

## Gate 5 — Native Windows tether boot

- `openra1n.exe` boots PongoOS and the device enumerates as `05AC:4141`.
- `PongoTransport` uploads `sep_racer.bin`, PTE and `kpf.bin`.
- Commands `modload`, `sep pte`, `sep pwn_pte`, `kpf-tethered` and `bootux` complete.
- The downgraded system boots and can be tether-booted again after a cold shutdown.

A failed gate must be fixed and repeated before a public release tag is created. Never infer hardware success from a green hosted build alone.
