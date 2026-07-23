from pathlib import Path

root = Path(__file__).resolve().parents[1]
path = root / "src" / "Palera1nWin.App" / "App.xaml.cs"
text = path.read_text(encoding="utf-8")
old_alias = "using CorePackageIntegrityReport = Palera1nWin.Core.Security.PackageIntegrityReport;"
new_alias = "using CorePackageIntegrityReport = Palera1nWin.Core.Security.PackageIntegrityReport;\nusing CorePackageIntegrityVerifier = Palera1nWin.Core.Security.PackageIntegrityVerifier;"
if old_alias not in text:
    raise SystemExit("Core package report alias not found")
text = text.replace(old_alias, new_alias, 1)
old_ctor = "integrity = await new PackageIntegrityVerifier().VerifyAsync().ConfigureAwait(true);"
new_ctor = "integrity = await new CorePackageIntegrityVerifier().VerifyAsync().ConfigureAwait(true);"
if old_ctor not in text:
    raise SystemExit("Core package verifier construction not found")
text = text.replace(old_ctor, new_ctor, 1)
path.write_text(text, encoding="utf-8", newline="\n")
print("Aliased both core package integrity types.")
