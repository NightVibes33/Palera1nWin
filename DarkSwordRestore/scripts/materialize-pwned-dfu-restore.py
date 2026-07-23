from __future__ import annotations

from pathlib import Path


ROOT = Path(__file__).resolve().parents[2]
SOURCE = ROOT / "DarkSwordRestore" / "src" / "DarkSwordRestore.Core" / "RestoreServices.cs"


def replace_exact(text: str, old: str, new: str, expected: int, label: str) -> str:
    count = text.count(old)
    if count != expected:
        raise SystemExit(f"{label}: expected {expected} occurrence(s), found {count}")
    return text.replace(old, new)


def main() -> int:
    text = SOURCE.read_text(encoding="utf-8")
    if "private async Task<AppleDeviceSnapshot> EnterPwnedDfuAsync(" in text:
        print("RestoreServices.cs already contains the materialized pwned-DFU pipeline.")
        return 0

    text = replace_exact(
        text,
        """            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.GeneratingShcBlock, 20, \"Capturing pre-restore SHC block\", \"Creating the initial restore-only SHC checkpoint.\");""",
        """            await EnterPwnedDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            Report(RestoreStage.GeneratingShcBlock, 20, \"Capturing pre-restore SHC block\", \"Creating the initial restore-only SHC checkpoint from verified pwned DFU.\");""",
        1,
        "initial pre-SHC handoff",
    )

    text = replace_exact(
        text,
        """            await PrepareDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);""",
        """            await PrepareDfuAsync(session, log, cancellationToken).ConfigureAwait(false);
            await EnterPwnedDfuAsync(session, log, cancellationToken).ConfigureAwait(false);""",
        3,
        "restore/post-SHC/PTE pwned-DFU handoffs",
    )

    text = replace_exact(
        text,
        """        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 35, \"Testing checkm8 and PongoOS\", \"Running the non-destructive ECID-bound hardware gate.\"));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, \"Hardware gate passed\", $\"PongoOS bridge verified for {identity.ProductType} ECID {identity.NormalizedEcid}.\"));""",
        """        progress?.Report(new RestoreProgress(RestoreStage.WaitingForDfu, 20, \"Testing pwned DFU\", \"Running checkm8 without uploading PongoOS and verifying the turdus-compatible PWND:[yolo] marker.\"));
        var pwned = await EnterPwnedDfuAsync(null, log, cancellationToken).ConfigureAwait(false);
        if (!pwned.MatchesIdentity(identity.ProductType, identity.NormalizedEcid))
            throw new DarkSwordException(RestoreStage.Preflight, \"The physical device identity changed during the pwned-DFU hardware gate.\");
        progress?.Report(new RestoreProgress(RestoreStage.BootingPongo, 55, \"Testing PongoOS\", \"Booting PongoOS only after the exact pwned-DFU handshake passed.\"));
        await BootPongoAsync(log, cancellationToken).ConfigureAwait(false);
        progress?.Report(new RestoreProgress(RestoreStage.Completed, 100, \"Hardware gate passed\", $\"Pwned DFU and PongoOS bridge verified for {identity.ProductType} ECID {identity.NormalizedEcid}.\"));""",
        1,
        "hardware gate",
    )

    insertion_point = """    private static void RequireExactIdentity(AppleDeviceSnapshot dfu, IpswInspectionResult inspection)
"""
    method = """    private async Task<AppleDeviceSnapshot> EnterPwnedDfuAsync(
        RestoreSession? session,
        Action<string>? log,
        CancellationToken cancellationToken)
    {
        var current = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
        if (current.Mode != AppleDeviceMode.Dfu)
            throw new DarkSwordException(RestoreStage.WaitingForDfu,
                $\"The turdus restore stage requires clean DFU before checkm8; current mode is {current.Mode}.\");
        if (!current.HasExactIdentity)
            throw new DarkSwordException(RestoreStage.WaitingForDfu,
                \"DFU was detected, but ProductType/ECID could not be read before the pwned-DFU stage.\");
        if (session is not null && !session.MatchesBoundIdentity(current))
            throw new DarkSwordException(RestoreStage.Preflight,
                \"The connected DFU device does not match the identity-bound downgrade session.\");

        var openRa1nCore = Path.Combine(_tools.Root, \"openra1n-core.exe\");
        if (!File.Exists(openRa1nCore))
            throw new DarkSwordException(RestoreStage.Preflight,
                \"The release is missing openra1n-core.exe required for the native pwned-DFU stage.\");

        try
        {
            await _runner.RunAsync(
                openRa1nCore,
                [DowngradeStagePlan.PwnedDfuOnlyArgument],
                _tools.Root,
                log,
                cancellationToken,
                timeout: TimeSpan.FromMinutes(3)).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw new DarkSwordException(RestoreStage.WaitingForDfu,
                \"The Windows-native checkm8 stage did not reach pwned DFU. No restore command was started.\", exception);
        }

        var irecovery = Path.Combine(_tools.Root, \"irecovery.exe\");
        var deadline = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(35);
        var last = AppleDeviceSnapshot.Disconnected;
        ToolResult? query = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            last = await _devices.ProbeAsync(cancellationToken).ConfigureAwait(false);
            if (last.Mode == AppleDeviceMode.Pongo)
                throw new DarkSwordException(RestoreStage.WaitingForDfu,
                    \"The pwned-DFU-only stage unexpectedly entered PongoOS. The destructive restore was blocked.\");

            if (last.Mode == AppleDeviceMode.Dfu && last.HasExactIdentity)
            {
                if (session is not null && !session.MatchesBoundIdentity(last))
                    throw new DarkSwordException(RestoreStage.Preflight,
                        \"The physical device identity changed after checkm8. The destructive restore was blocked.\");

                query = await _runner.RunAsync(
                    irecovery,
                    [\"-q\"],
                    _tools.Root,
                    null,
                    cancellationToken,
                    requireZeroExitCode: false,
                    timeout: TimeSpan.FromSeconds(10)).ConfigureAwait(false);
                if (query.Success && DowngradeStagePlan.IsPwnedDfuQueryOutput(query.CombinedOutput))
                {
                    try { log?.Invoke($\"[DarkSword] Verified turdus-compatible pwned DFU marker {DowngradeStagePlan.RequiredPwnedDfuMarker} for {last.ProductType} ECID {last.NormalizedEcid}. PongoOS was not uploaded.\"); } catch { }
                    return last;
                }
            }

            await Task.Delay(500, cancellationToken).ConfigureAwait(false);
        }

        var queryOutput = query?.CombinedOutput?.Trim();
        if (string.IsNullOrWhiteSpace(queryOutput)) queryOutput = \"no irecovery query output\";
        throw new DarkSwordException(RestoreStage.WaitingForDfu,
            $\"checkm8 returned, but the exact {DowngradeStagePlan.RequiredPwnedDfuMarker} marker was not verified. Last mode={last.Mode}; service={last.Service ?? \"unknown\"}. {queryOutput}\");
    }

"""
    text = replace_exact(text, insertion_point, method + insertion_point, 1, "pwned-DFU method insertion")

    if text.count("await EnterPwnedDfuAsync(session, log, cancellationToken)") != 4:
        raise SystemExit("Materialized downgrade pipeline does not contain exactly four destructive-stage pwned-DFU handoffs.")
    if text.count("await EnterPwnedDfuAsync(null, log, cancellationToken)") != 1:
        raise SystemExit("Materialized hardware gate does not contain exactly one pwned-DFU validation handoff.")

    SOURCE.write_text(text, encoding="utf-8", newline="\n")
    print("Materialized the verified pwned-DFU downgrade pipeline in RestoreServices.cs.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
