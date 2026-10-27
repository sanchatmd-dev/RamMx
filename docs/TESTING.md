# Verification record — v0.1.0 Preview

Test host: Windows 11 Home x64, .NET SDK 10.0.403. Results describe this host, not every Windows build or installed application. No real user application was terminated or uninstalled and no real application cache was deleted during destructive tests.

## Automated checks

`./scripts/build.ps1 -Publish` restores the solution, builds Release, runs xUnit integration tests and publishes a self-contained win-x64 ZIP. Local results on 2026-10-27: **72 passed, 0 failed, 0 skipped**. Release compilation: **0 warnings, 0 errors**. TRX output is in `tests/RamMx.Tests/TestResults/` and uploaded by CI.

Coverage includes:

- Critical, protected, unresolved, system and shared-runtime process policies; stale PID/creation/path rejection.
- Private resident memory versus commit, unavailable metrics, profile/Keep/foreground rules and sustained pressure thresholds.
- SQLite preference persistence, incomplete-action recovery and bounded settings import.
- Real Win32 memory counters and process identity queries.
- Disposable WPF application: graceful close, refused close without automatic escalation, then a separate force-stop operation.
- Disposable HKCU EXE uninstaller registration: launch, removal and fresh inventory verification.
- Disposable cache trees: selected deletion, personal data preservation, replaced/modified/locked files, new files after preview, outside-root attempts and hard links.
- Uninstaller command parsing, broker request expiry/nonce/payload/operation validation and unsigned executable evidence.
- GUID-named Windows SystemApps protection, path-prefix boundaries, shared components and non-finite settings values.
- Incremental process row updates preserve identity, refresh action policy and avoid redundant display notifications.
- Background review excludes foreground/system groups and high RAM alone, while explaining temporary paths and measured growth.
- Unknown process protection denies actions. Cancelled native-operation waits leave the underlying operation running and produce no rollback claim.
- Cache scan limits include accepted files, visited entries, directory depth, skipped messages and browser profile discovery; cancellation leaves fixture files untouched.
- STA WPF tests cover the selected background-review inspection/evidence tab, unchanged-batch notifications and hidden polling that persists metrics without refreshing visual collections.
- Imported null profile lists and case-insensitive app identity; stable accessible process names while metric cells still change.

`dotnet list RamMx.sln package --vulnerable --include-transitive` reported no known vulnerable packages in the checked dependency graph. This is an advisory lookup, not a guarantee of vulnerability-free software.

## Native UI smoke checks

Opened the published native Windows executable and inspected rendered screens and accessibility trees. Overview metrics/graph, process table/actions, installed applications and cache page rendered. Fixed title contrast, dark combo styling and window sizing/scrolling discovered during these checks. Read-only inventory exposed a GUID-named SystemApps package; added a location-based protection rule and regression tests, then verified that its uninstall button was disabled. Light theme rendered and persisted across closing/reopening the published app. A read-only Chrome cache scan completed at the configured 20,000-file cap; no cache was deleted.

UI deletion/uninstall of real applications was intentionally not exercised. Destructive integration tests use disposable fixtures. No Windows security/UAC prompts were automated.

On the resumed build, a disposable 32 MiB process ran without a window from its own temporary fixture directory. It appeared in the review-candidates table with the temporary-path explanation. Selecting it and inspecting opened the evidence tab and displayed the unsigned-file result without a malware verdict. The fixture self-exits after five minutes.

## Performance

Use the read-only aggregate tool while RamMx is running:

```powershell
dotnet run --project tools/RamMx.Diagnostics -c Release -- <RamMx-PID> 120 20
```

The optional final argument excludes warm-up seconds. The tool records normalized CPU, logical processor count, private working set/commit, handles and sampler duration without exporting application names or paths. Sampler timing covers the diagnostic tool's `ReadProcesses()` call without inventory, not the full app/UI update. Measure with a quiet system and report whether the main window is visible or in the tray. A short measurement does not establish absence of leaks.

Earlier non-controlled measurements during UI verification ranged around 3.5–7.2% CPU. A sampled-thread-time trace contained substantial Windows UI Automation activity and waiting; these percentages are wall time, not CPU attribution. Source improvements include indexed telemetry, incremental rows, stable accessible row names, skipped unchanged batches and no visual refresh while hidden. Accessibility remains enabled.

A resumed visible-window run on 12 logical processors, 20 seconds warm-up plus 120 seconds measured, observed **0.34% average normalized CPU**, **57.98 MiB peak private working set**, **97.05 MiB peak private commit**, handles **857→845**, and diagnostic sampler median/p95 **26.18/35.47 ms**. No UI automation calls were made during that interval. Different machine activity and tool attachment mean this is not a controlled before/after benchmark or a guaranteed footprint on every system.

The tray run used the same warm-up/duration and 12 logical processors: **0.052% average normalized CPU**, **43.68 MiB peak private working set**, **81.12 MiB peak private commit**, handles **821→816**, sampler median/p95 **25.16/35.51 ms**. The main window was hidden and no UI automation calls occurred during measurement. Reopening the executable restored the original process; the second invocation exited rather than opening another instance.

## Remaining production gates

- Real MSI and MSIX uninstall fixtures, cancellation, child uninstallers, reboot-required and package-data-loss semantics in disposable VMs.
- Elevated broker UAC approval/denial, different security contexts, tampering/replay and protected installation ACL validation.
- Actual Defender provider availability, scanning/remediation, policy variations and cancellation.
- Additional junction/reparse races, disk-full/IO failure, interrupted mutation and recovery scenarios.
- Vendor/version-specific cache layouts and more protected-data fixtures.
- Twenty-four-hour soak, multiple DPI settings, high contrast, screen readers and additional Windows builds.
- Publisher code signing and a signed installer/update mechanism.

The presence of a button or adapter does not mean every external provider flow passed these gates. Native uninstall is not reversible, and cache cleanup covers five explicit recipes rather than all leftovers of all software.
