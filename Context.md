# RamMx Development Context

## Product intent

Windows native app for a Thai-speaking user to understand RAM usage, identify OS/background processes, close unwanted apps, uninstall software, clean owned disposable caches and retain preferences. Honest measurements and workload reduction matter more than a lower-looking memory number.

Repository: `sanchatmd-dev/RamMx`. Working v0.1.0 preview, not a claim that every production gate in the blueprint has passed.

## Resumed checkpoint — 2026-10-27

The user resumed work on 2026-10-27. `docs/CHECKPOINT.md` preserves the historical paused state; current verification is recorded in `docs/TESTING.md`.

## Stack and source map

- .NET 10, C#, WPF; API contract target `net10.0-windows10.0.19041.0`, supported product target Windows 11 x64.
- `RamMx.Core`: models, action policy, recommendation/behaviour rules and settings validation.
- `RamMx.Windows`: Win32 telemetry, registry/MSIX inventory, native uninstall, Authenticode, file-handle cleanup and elevation protocol.
- `RamMx.Persistence`: Microsoft.Data.Sqlite, parameterized queries, preferences, metrics and action journal.
- `RamMx.App`: WPF UI, commands, confirmation dialogs, chart, tray and lifecycle.
- `tests/RamMx.Tests`: xUnit policy/persistence/filesystem/Windows integration tests.
- `tests/MemoryFixture`: disposable WPF process and per-user EXE uninstaller fixture.
- `tools/RamMx.Diagnostics`: read-only aggregate footprint benchmark; exports no process names/paths.
- `scripts/measure-cpu.ps1`: passive RamMx process counters, with no UI Automation attachment or second process-list sampler.
- `scripts/build.ps1`: build/test/self-contained ZIP packaging.

Boost RAM source: `Core/BoostPolicy.cs` owns sampled activity and exclusions; `Windows/BoostService.cs` coordinates fresh review, durable intent, mutation and restoration; `Windows/BoostProcessActions.cs` holds validated native handles; `App/MainViewModel.Boost.cs` owns preview, profile choices and measured results. Persistence adds `boost_restore` independently of removable history.

Broker mode shares the app executable (`RamMx.exe --broker ...`) rather than a separate project. It accepts only ForceStop/DefenderScan, verifies the pipe peer, session, creation time, executable path, nonce, expiration and targets, then shows its own confirmation. No generic shell-command/file-delete endpoint. Same-user attackers are not comprehensively defeated by these checks: production requires signed binaries, protected installation ACLs and adversarial VM review.

## Invariants

1. Available RAM, private working set, working set, commit and disk free differ. Preserve units and unavailable/partial coverage; do not sum shared working sets into physical usage.
2. No default EmptyWorkingSet, standby purge, pagefile shrinking or registry cleaner. Boost may trim an explicitly approved, revalidated app once per session only when the user selects the non-persisted option. Never suspend arbitrary processes.
3. Never choose a destructive target by name/PID alone. Match PID + creation time + path; reopen and recheck immediately before action.
4. Critical/protected/OS/security/shared-runtime/unresolved groups fail closed. User trust/Keep cannot override hard policy.
5. Graceful close never becomes force stop after timeout. Force requires separate confirmation.
6. Shared-runtime parentage is not definitive ownership; svchost is not a service-specific stop target.
7. Close recommendations require explicit unused-in-profile selection, excluding Keep and foreground. High RAM or idle CPU does not prove unnecessary/malicious activity.
8. Native uninstall uses exact registration/package identity and fresh inventory. Never query Win32_Product, which can initiate MSI repair checks.
9. Never elevate arbitrary user-writable uninstall commands. Ambiguous quoting, script hosts or missing uninstallers go to Windows Installed apps.
10. Cleanup roots are compiled allowlisted recipes. Hold directory handles against rename/delete; reject reparse points/hard links; revalidate file ID/volume/size/write time; delete through the validated handle.
11. No cleanup of documents, cookies, profiles, history, settings, databases, Windows Installer cache, WinSxS, WindowsApps, driver stores or shared runtimes.
12. Cache deletion is permanent and separately confirmed. No promise of undo; quarantine would not count as disk reclaimed.
13. Journal intent before mutation. Incomplete actions become UnknownOutcome at restart and are never replayed automatically. Boost restoration is the exception for undoing a prior change: revalidate the exact process and restore only if its priority still equals the value RamMx applied. Never reapply Boost on restart.
14. SQLite preferences do not authorize privileged execution. Bound imports and IPC payloads.
15. No automatic uploads of files, hashes, process lists or logs. No LLM executes commands.

## Data flows

One non-overlapping UI timer samples every two seconds by default, ten seconds while hidden/minimized. Hidden polling retains telemetry/history without refreshing visual collections. A semaphore serializes manual and timer sampling. Metrics run off the dispatcher. Selection is restored by identity. Inventory refreshes on demand/after uninstall, not every tick. Package/path lookup is indexed; windows are enumerated once per sample. Polling does not inspect executable metadata or signatures. Process rows retain their objects and notify only changed displayed values; membership/order updates are incremental and accessible names remain stable. Other collections skip equal batches. In-flight background samples are not applied during action confirmation, and stop operations capture a target snapshot.

The review-candidates table refreshes every ten seconds with explanations for unknown/temporary paths or observed growth after baseline. Explicit inspection opens the evidence tab. Protection status is nullable; an unreadable status denies stopping. Cancellation of an MSIX wait leaves the native operation running and reports UnknownOutcome. Reopening the executable signals the existing instance to restore from tray.

Memory polling first uses PSAPI EX2/EX. When private working set is unavailable, a lazy PDH snapshot reads Process ID, Working Set, Working Set - Private and Private Bytes from Windows 11's Process V2 counterset in one collection using language-neutral paths. Its name:PID instances avoid legacy Process array name collisions. Exact instance joins, valid counter status, nonnegative values, unique PID and matching process name/PID suffix are required; duplicate array names are rejected. Memory provenance appears in process details. A process-local, locked query reuses exactly four counter handles; every poll collects fresh values into new dictionaries. Array buffers are released each poll. Collection/array failures discard the query and return unavailable, never previous values. SafeHandle and process-exit cleanup release the query; the next read can reopen it. Performance data is display-only and does not supply action identity, trust, critical/protection status or stop permission. System/protected processes can remain unclassified when their image paths are inaccessible even when their RAM is readable. Values below 1 MiB use KiB/B to avoid rounding small resident sets to zero.

System metric aggregates persist once per minute for seven days. Completed action history is bounded to 1,000 entries; unfinished/unknown outcomes are retained. Database size is checked before new destructive actions. SQLite uses WAL/checkpoints. Preferences survive without tray/startup.

Boost observes foreground state, total CPU time, total I/O bytes and PID/creation-validated parentage. More than 2% of one logical CPU or 64 KiB/s I/O resets the observed idle interval; missing counters, a sampling gap over 90 seconds, clock reversal or a new process instance also reset it. The default is 15 minutes (5–120 configurable). Sampling is evidence of low activity, not proof that work is dispensable. An explicit per-profile allowlist and global Keep list precede fresh checks at execution. Workload ancestors/descendants are protected; ancestry must resolve to a verified Windows launcher. Unknown ownership fails closed, so some otherwise ordinary applications remain unavailable.

Boost uses normal-user Get/SetProcessInformation with ProcessMemoryPriority=Low (2), leaving CPU priority unchanged. It saves the original priority in SQLite before each native change; failed journal writes prevent that target's mutation. Optional EmptyWorkingSet requires extra handle rights, runs once, and is never retried for a process already in the session. At most 100 requested targets / 200 pending restores are allowed. Exact PID + creation time + image path and current critical/protection state are rechecked on native handles. No elevated Boost path exists.

While the Boost page is visible, only its displayed cells are updated; unrelated process tables/charts stay idle, while telemetry, activity tracking and aggregate persistence continue. Navigating away refreshes the process table. Tests set a known disposable-fixture priority because hosted CI may launch children with Low already; production skips already-low targets without trimming them.

Boost RAM/idle label strings are calculated once for each new row snapshot and reused by bindings/accessibility reads until the next sample. New values and unreadable/partial states still update normally. The DataGrid and its native accessibility remain enabled. Performance measurements must distinguish a connected desktop inspection client from a passive run: no tool calls during a measurement does not mean the client has disconnected. The earlier 5.46% visible figure included such a connection; see the correction and comparison in `docs/TESTING.md`.

Polling attempts restoration when activity resumes, eligibility is removed or the profile changes. Explicit end, normal process exit and next-start recovery also restore. Closing to tray is not exit. Restore failures remain pending and visible as a count; explicit restore exposes details and retries. External priority changes are preserved. Pending restore records survive history deletion/pruning; activity age and trim selection are not persisted. Available RAM before/after is a machine-wide observation, not attributed solely to Boost. The browser-help button is guidance only, with no extension, tab API or automatic settings changes.

Recipes: Chrome/Edge profile Cache/Cache_Data, Code Cache, GPUCache; VS Code Cache/Cache_Data, CachedData, GPUCache, Code Cache; Discord/Slack Cache/Cache_Data, Code Cache, GPUCache. Browser profile directory patterns are restricted. A recipe does not imply all vendor versions have been tested.

Scan budgets are 20,000 accepted files, 40,000 visited entries, depth 64 and 2,000 skipped messages. Profile discovery shares the visit budget. Traversal is iterative and queued directory ancestors are revalidated/locked before enumeration. Any budget truncation is shown as a partial scan.

## Build and verification

Run `./scripts/build.ps1 -Publish` on Windows with .NET 10 SDK. Build the whole solution so MemoryFixture exists. Local SDK is selected from `.tools/dotnet` when present; DOTNET_ROOT is set for child fixtures. Tests never need to terminate a user's real app.

Windows-native execution is required, not just cross-platform compilation. Cover privilege denial, stale identities, uninstaller children, package-data semantics, locked files, junction races, disk full and crash recovery when touching relevant modules.

Keep `docs/TESTING.md` explicit about automated, integration, UI and unverified cases. Do not claim execution from code inspection. Exclude SDKs, artifacts, binaries, databases, private research and certificates from source commits.

## Remaining production work

- Real publisher certificate, signed installer/update pipeline, broker directory ACL hardening and adversarial IPC tests across security contexts.
- Isolated MSI/MSIX uninstall fixtures, UAC approval/denial and Defender provider/remediation scenarios.
- Exhaustive junction/reparse races and filesystem recovery semantics.
- Vendor/version-specific cache validation beyond disposable fixtures; protected-data tests for new recipes.
- 24-hour soak, additional DPI/high-contrast/screen-reader and Windows build coverage.
- More exact app/runtime ownership and service details; no generic shared-host kill fallback.
- Reversible startup adapters/service controls only after dependency/scope testing.

These are limitations, not background tasks or automations already scheduled.
