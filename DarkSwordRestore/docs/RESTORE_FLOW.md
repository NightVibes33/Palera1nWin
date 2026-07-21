# Restore state machine

```text
Idle
  -> Preflight
  -> WaitingForDfu
  -> InstallingDfuDriver
  -> EnteringPwnedDfu
  -> GeneratingShcBlock (pre-restore)
  -> WaitingForDfu
  -> EnteringPwnedDfu
  -> RestoringFirmware
  -> WaitingForDfu
  -> EnteringPwnedDfu
  -> GeneratingShcBlock (post-restore)
  -> WaitingForDfu
  -> EnteringPwnedDfu
  -> GeneratingPteBlock
  -> WaitingForDfu
  -> InstallingDfuDriver
  -> EnteringPwnedDfu / BootingPongo
  -> LoadingSepExploit
  -> LoadingKernelPatchfinder
  -> BootingXnu
  -> Completed
```

## Failure behavior

- A cancelled native process is terminated with its child process tree.
- The last completed stage is written to `session.json`.
- The application never silently continues after a failed SHC/PTE or restore command.
- Generated files are discovered by comparing pre-stage and post-stage directory snapshots rather than assuming a fixed ECID filename.
- The UI preserves logs and presents the exact stage that failed.

## Tether boot-only flow

```text
Select PTE block
  -> WaitingForDfu
  -> InstallingDfuDriver
  -> openra1n checkm8 + PongoOS
  -> PongoTransport uploads SEP/PTE/KPF
  -> bootux
```
