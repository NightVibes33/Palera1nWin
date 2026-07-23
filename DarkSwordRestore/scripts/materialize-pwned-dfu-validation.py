from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SMOKE = ROOT / "DarkSwordRestore" / "scripts" / "smoke-test-package.ps1"
README = ROOT / "README.md"


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise SystemExit(f"{label}: expected {expected} occurrence(s), found {count}")
    return text.replace(old, new)


def patch_smoke() -> None:
    text = SMOKE.read_text(encoding="utf-8")
    if 'Assert-BinaryString "toolchain\\openra1n-core.exe" "--pwned-dfu-only"' in text:
        print("Packaged pwned-DFU validation is already materialized.")
        return

    text = replace_exact(
        text,
        """        \"ValidateDfuToPongoAsync\",
        \"DarkSwordJailbroken\"""",
        """        \"ValidateDfuToPongoAsync\",
        \"DowngradeStagePlan\",
        \"--pwned-dfu-only\",
        \"PWND:[yolo]\",
        \"DarkSwordJailbroken\"""",
        1,
        "managed capability list",
    )

    anchor = """    Assert-BinaryString \"toolchain\\openra1n.exe\" \"DFU remains owned by Windows\"
    Assert-BinaryDoesNotContain \"toolchain\\openra1n.exe\" \"windows\\palera1n.ps1\"
    Assert-BinaryDoesNotContain \"toolchain\\openra1n.exe\" \"wdi-simple.exe\"
"""
    replacement = anchor + """    Assert-BinaryString \"toolchain\\openra1n-core.exe\" \"--pwned-dfu-only\"
    Assert-BinaryString \"toolchain\\openra1n-core.exe\" \"PWND:[yolo]\"
    Assert-BinaryString \"toolchain\\openra1n-core.exe\" \"Pwned DFU ready\"
    Assert-BinaryDoesNotContain \"toolchain\\openra1n-core.exe\" \"YOLO:checkra1n\"

    $coreUsage = Invoke-CapturedProcess -FilePath (Join-Path $toolchain \"openra1n-core.exe\") -ArgumentList @(\"--invalid-smoke-option\") -Name \"openra1n-core-usage\"
    if ($coreUsage.ExitCode -eq 0 -or -not $coreUsage.Output.Contains(\"--pwned-dfu-only\", [System.StringComparison]::OrdinalIgnoreCase)) {
        throw \"openra1n-core.exe did not expose the separate pwned-DFU-only mode. Exit code: $($coreUsage.ExitCode)\"
    }
    Write-Status \"OK native pwned-DFU-only mode rejects invalid arguments before USB access\"
"""
    text = replace_exact(text, anchor, replacement, 1, "native smoke assertions")
    SMOKE.write_text(text, encoding="utf-8", newline="\n")
    print("Materialized package-level pwned-DFU validation.")


def patch_readme() -> None:
    text = README.read_text(encoding="utf-8")
    marker = "verified `PWND:[yolo]` pwned DFU"
    if marker in text:
        print("README already documents the corrected pwned-DFU restore path.")
        return

    ownership_anchor = """- Jailbreak, the non-destructive DFU/Pongo test, and Downgrade all use this same initial Windows-native pipeline.

Physical validation is still required.
"""
    ownership_replacement = """- Jailbreak and the non-destructive hardware test use the Windows-native Pongo launch path.
- The destructive DarkSword SHC/restore/PTE stages stop earlier in verified `PWND:[yolo]` pwned DFU, matching the pinned turdus restore fork's required input mode.
- PongoOS is not uploaded during SHC capture, firmware restore, or PTE generation; it is used again only for the Pongo half of the hardware test and final tether boot.

Physical validation is still required.
"""
    text = replace_exact(text, ownership_anchor, ownership_replacement, 1, "README ownership model")

    downgrade_anchor = """The current Windows SEP-block restore backend is enabled only for supported **A9-class DarkSword catalog targets**. The primary target is the iPad 5th generation (`iPad6,11` / `iPad6,12`). Broader jailbreak support does not mean broader downgrade support.
"""
    downgrade_replacement = downgrade_anchor + """
For A9, **Start Downgrade** now enforces the original restore-state boundary before every destructive native operation:

1. Run Windows-native checkm8 with `openra1n-core.exe --pwned-dfu-only`.
2. Verify DFU remains `05AC:1227` and `irecovery -q` reports the exact turdus-compatible `PWND:[yolo]` marker.
3. Only then run the requested `turdus_merula.exe` SHC, restore, or PTE operation.
4. Abort before erasing if PongoOS appears early, the marker is missing, or ProductType/ECID changes.

"""
    text = replace_exact(text, downgrade_anchor, downgrade_replacement, 1, "README A9 sequence")

    component_old = """| `toolchain/openra1n-core.exe` | Production Windows-native DFU/checkm8/PongoOS core used before any WSL handoff |"""
    component_new = """| `toolchain/openra1n-core.exe` | Production Windows-native checkm8 core with separate verified pwned-DFU-only and PongoOS modes |"""
    text = replace_exact(text, component_old, component_new, 1, "README core component")

    README.write_text(text, encoding="utf-8", newline="\n")
    print("Materialized corrected pwned-DFU documentation.")


def main() -> int:
    patch_smoke()
    patch_readme()
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
