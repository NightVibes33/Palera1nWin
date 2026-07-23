from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / "DarkSwordRestore" / "scripts" / "smoke-test-package.ps1"
text = path.read_text(encoding="utf-8")
old = '''        "ValidateDfuToPongoAsync",
        "DowngradeStagePlan",
        "--pwned-dfu-only",
        "PWND:[yolo]",
        "DarkSwordJailbroken"'''
new = '''        "ValidateDfuToPongoAsync",
        "DowngradeStagePlan",
        "DarkSwordJailbroken"'''
count = text.count(old)
if count != 1:
    raise SystemExit(f"Expected one managed capability block, found {count}")
path.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")
print("Removed invalid managed constant string assertions; native and xUnit checks remain.")
