from pathlib import Path

root = Path(__file__).resolve().parents[2]
path = root / "src" / "Palera1nWin.App" / "Views" / "DowngradeView.xaml.cs"
text = path.read_text(encoding="utf-8")
old = '''        _driver = new DfuDriverService(_runner, _tools);
        _orchestrator = new DarkSwordOrchestrator(_tools, _runner, _inspector, _monitor, _sessions, _driver);

        Loaded += DowngradeView_Loaded;'''
new = '''        _driver = new DfuDriverService(_runner, _tools);
        _orchestrator = new DarkSwordOrchestrator(_tools, _runner, _inspector, _monitor, _sessions, _driver);

        // Activate the DarkSword Quick Actions surface immediately after every
        // dependency and XAML control exists. This hides the legacy downloader,
        // readiness, confirmation, diagnostics, and restore-control maze while
        // keeping those controls alive behind the four-action workflow.
        WireDowngradeExperienceHooks();
        InitializeUiHardening();

        Loaded += DowngradeView_Loaded;'''
if text.count(old) != 1:
    raise SystemExit(f"Expected exactly one constructor activation point, found {text.count(old)}")
path.write_text(text.replace(old, new, 1), encoding="utf-8", newline="\n")
print("Reconnected the DarkSword Quick Actions UI during DowngradeView construction.")
