# Verification record — v0.1.0 Preview

Test host: Windows 11 Home x64, .NET SDK 10.0.403. Results describe this host, not every Windows build or installed application. No real user application was terminated or uninstalled and no real application cache was deleted during destructive tests.

## Automated checks

`./scripts/build.ps1 -Publish` restores the solution, builds Release, runs xUnit integration tests and publishes a self-contained win-x64 ZIP. Local results on 2026-10-27: **115 passed, 0 failed, 0 skipped**. Release compilation: **0 warnings, 0 errors**. TRX output is in `tests/RamMx.Tests/TestResults/` and uploaded by CI.

Coverage includes:

- Critical, protected, unresolved, system and shared-runtime process policies; stale PID/creation/path rejection.
- Private resident memory versus commit, unavailable metrics, profile/Keep/foreground rules and sustained pressure thresholds.
- SQLite preference persistence, incomplete-action recovery and bounded settings import.
- Real Win32 memory counters and process identity queries.
- PDH memory fallback: real private resident counters, exact instance joins, duplicate PID/array-name rejection, two live fixtures with the same executable name, failed/missing/negative/inconsistent values, valid zero, process-name/PID matching, partial coverage and unchanged action guards.
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

## RAM access fix — 2026-10-27

Normal-user telemetry and the published UI now read private working set for winlogon, dwm, atieclxx and both fontdrvhost instances on this host. The final native test observed 362/366 processes with private RAM available; this is a point-in-time result, not a promise of universal access. The UI showed the two fontdrvhost instances separately at 172 KiB and 16 KiB; dwm showed 28.6 MiB, a distinct private-commit value and Windows Performance Counters as its RAM source. Close/force/admin stop remained disabled for that unresolved system process.

The fallback uses Windows 11 Process V2. Legacy Process wildcard arrays can repeat instance names, which made name-based maps lose one same-name process. A regression test starts two disposable instances of the same fixture and requires both PID-specific readings; duplicate returned names fail closed. See Microsoft's [duplicate instance guidance](https://learn.microsoft.com/en-us/windows/win32/perfctrs/handling-duplicate-instance-names). Unavailable counters remain unavailable, and reading memory does not verify an inaccessible executable path.

A post-fix footprint check with the process detail page visible (5 s warm-up, 61.37 s measured, 12 logical processors, 29 samples) observed 3.40% normalized app CPU, 60.10 MiB peak private working set and sampler median/p95 76.24/324.18 ms. A minimized check (10 s warm-up, 31.84 s measured, 15 samples) observed 0.102% CPU, 52.86 MiB peak private working set, handles 913→902 and sampler median/p95 72.92/556.90 ms. No UI automation calls ran during either interval. These short, different-view runs are not a controlled comparison with the earlier dashboard measurements: the visible CPU increase remains a profiling concern, and the earlier 0.336% visible result must not be treated as the footprint of this change.

## Boost RAM — 2026-10-27

Added 28 cases to the previous 87-test suite. Policy coverage includes explicit profile approval, Keep, continuous activity sampling, CPU/I/O/foreground resets, missing data, clock gaps/reversal, PID reuse, protected work ancestors/descendants, ambiguous ancestry and settings bounds. SQLite tests reopen restore intent and verify that clearing history cannot erase it.

Native tests only modify disposable MemoryFixture processes: read/set memory priority, one-shot EmptyWorkingSet while the fixture remains alive, exact original-priority restoration, stale identity rejection, journal write failure before mutation, repeat-Boost suppression, foreground-triggered restoration and preservation of externally changed priority. Restore-journal removal failure retains the pending entry and succeeds on retry. Start-flow tests use a simulated quiet clock/history and desktop parent with the fixture's real native process identity; they do not establish 15 minutes of real-world inactivity for user applications.

STA WPF tests cover Boost navigation, saved choices for closed apps, Keep/profile persistence, disabled execution without eligible targets, and startup restoration from a persisted priority-change record. Startup recovery is simulated by seeding that record and changing the fixture priority; it is not a power-loss test.

The initial CI run exposed a test-environment assumption: hosted-runner children started with Low memory priority (2), while the local fixtures initially used Normal (5). Native tests now explicitly establish priority 4 for the disposable fixture before testing recovery, and also verify that an already-low target is skipped without a journal entry or trim. No production guard was relaxed. Additional regression tests require per-cell Boost notifications and no updates to inactive process tables/charts while Boost is visible; navigating back refreshes the process table.

The published native UI was inspected at approximately 1248 × 645 in the existing light theme: dashboard navigation, Boost table, protected Codex/Claude/ChatGPT/browser rows, 15-minute default, unchecked trim, disabled Start/Restore with no targets, and the browser guidance dialog. No real user app was boosted, trimmed, suspended or closed. Browser settings were not changed. No throughput improvement or amount of recovered RAM is claimed. Earlier footprint measurements above predate this feature and are not a benchmark of Boost.

A short visible Boost-page measurement after limiting inactive-table updates (5 s warm-up, 32.06 s measured, 12 logical processors) observed **5.46% normalized CPU**, **57.75 MiB peak private working set**, **95.36 MiB peak private commit**, handles **896→889**, and diagnostic sampler median/p95 **92.79/423.41 ms**. An earlier 30 s Boost run observed 7.07% CPU. These are uncontrolled measurements on a busy machine, with no UI automation calls during either interval; they do not prove the size of a performance improvement. Visible CPU remains a profiling concern. A separate sampled-thread-time trace showed waits and UI Automation dispatch, which does not establish CPU attribution.

With the same app minimized, a later 5 s warm-up / 31.96 s measurement observed **0.51% normalized CPU**, **66.44 MiB peak private working set**, **106.28 MiB peak private commit**, handles **885→886**, sampler median/p95 **81.61/389.00 ms**. No UI automation calls occurred during measurement. This followed the diagnostic trace and was not an isolated cold-start benchmark. Minimizing reduces visual work while activity tracking/restoration continues; longer performance and leak investigations remain open.

## Remaining production gates

- Real MSI and MSIX uninstall fixtures, cancellation, child uninstallers, reboot-required and package-data-loss semantics in disposable VMs.
- Elevated broker UAC approval/denial, different security contexts, tampering/replay and protected installation ACL validation.
- Actual Defender provider availability, scanning/remediation, policy variations and cancellation.
- Additional junction/reparse races, disk-full/IO failure, interrupted mutation and recovery scenarios.
- Vendor/version-specific cache layouts and more protected-data fixtures.
- Twenty-four-hour soak, multiple DPI settings, high contrast, screen readers and additional Windows builds.
- Publisher code signing and a signed installer/update mechanism.

The presence of a button or adapter does not mean every external provider flow passed these gates. Native uninstall is not reversible, and cache cleanup covers five explicit recipes rather than all leftovers of all software.
