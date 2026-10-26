# RamMx Development Context

## Product intent

Windows native app for a Thai-speaking user to understand RAM usage, identify OS/background processes, close unwanted apps, uninstall software, clean owned disposable caches and retain preferences. Honest measurements and workload reduction matter more than a lower-looking memory number.

Repository: `sanchatmd-dev/RamMx`. Working v0.1.0 preview, not a claim that every production gate in the blueprint has passed.

## Paused checkpoint — 2026-10-27

The user requested a checkpoint and stop. Do not resume implementation until asked. See `docs/CHECKPOINT.md` for the exact distinction between tested packaged code and pending changes.

## Stack and source map

- .NET 10, C#, WPF; API contract target `net10.0-windows10.0.19041.0`, supported product target Windows 11 x64.
- `RamMx.Core`: models, action policy, recommendation/behaviour rules and settings validation.
- `RamMx.Windows`: Win32 telemetry, registry/MSIX inventory, native uninstall, Authenticode, file-handle cleanup and elevation protocol.
- `RamMx.Persistence`: Microsoft.Data.Sqlite, parameterized queries, preferences, metrics and action journal.
- `RamMx.App`: WPF UI, commands, confirmation dialogs, chart, tray and lifecycle.
- `tests/RamMx.Tests`: xUnit policy/persistence/filesystem/Windows integration tests.
- `tests/MemoryFixture`: disposable WPF process and per-user EXE uninstaller fixture.
- `tools/RamMx.Diagnostics`: read-only aggregate footprint benchmark; exports no process names/paths.
- `scripts/build.ps1`: build/test/self-contained ZIP packaging.

Broker mode shares the app executable (`RamMx.exe --broker ...`) rather than a separate project. It accepts only ForceStop/DefenderScan, verifies the pipe peer, session, creation time, executable path, nonce, expiration and targets, then shows its own confirmation. No generic shell-command/file-delete endpoint. Same-user attackers are not comprehensively defeated by these checks: production requires signed binaries, protected installation ACLs and adversarial VM review.

## Invariants

1. Available RAM, private working set, working set, commit and disk free differ. Preserve units and unavailable/partial coverage; do not sum shared working sets into physical usage.
2. No default EmptyWorkingSet, standby purge, pagefile shrinking or registry cleaner.
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
13. Journal intent before mutation. Incomplete actions become UnknownOutcome at restart and are never replayed automatically.
14. SQLite preferences do not authorize privileged execution. Bound imports and IPC payloads.
15. No automatic uploads of files, hashes, process lists or logs. No LLM executes commands.

## Data flows

One non-overlapping UI timer samples every two seconds by default, ten seconds while hidden. A semaphore serializes manual and timer sampling. Metrics run off the dispatcher. Selection is restored by identity. Inventory refreshes on demand/after uninstall, not every tick. Package/path lookup is indexed; windows are enumerated once per sample. Polling does not inspect executable metadata or signatures. Process rows retain their objects and notify only changed displayed values; membership/order updates are incremental. Other collections use one reset per batch. In-flight background samples are not applied during action confirmation, and stop operations capture a target snapshot.

System metric aggregates persist once per minute for seven days. Completed action history is bounded to 1,000 entries; unfinished/unknown outcomes are retained. Database size is checked before new destructive actions. SQLite uses WAL/checkpoints. Preferences survive without tray/startup.

Recipes: Chrome/Edge profile Cache/Cache_Data, Code Cache, GPUCache; VS Code Cache/Cache_Data, CachedData, GPUCache, Code Cache; Discord/Slack Cache/Cache_Data, Code Cache, GPUCache. Browser profile directory patterns are restricted. A recipe does not imply all vendor versions have been tested.

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
