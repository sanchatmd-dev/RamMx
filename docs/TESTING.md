# Verification record — v0.1.0 Preview

Test host: Windows 11 Home x64, .NET SDK 10.0.403. Results describe this host, not every Windows build or installed application. No real user application was terminated or uninstalled and no real application cache was deleted during destructive tests.

## Automated checks

`./scripts/build.ps1 -Publish` restores the solution, builds Release, runs xUnit integration tests and publishes a self-contained win-x64 ZIP. Local results: **53 passed, 0 failed, 0 skipped**. Release compilation: **0 warnings, 0 errors**. TRX output is in `tests/RamMx.Tests/TestResults/` and uploaded by CI.

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

`dotnet list RamMx.sln package --vulnerable --include-transitive` reported no known vulnerable packages in the checked dependency graph. This is an advisory lookup, not a guarantee of vulnerability-free software.

## Native UI smoke checks

Opened the published native Windows executable and inspected rendered screens and accessibility trees. Overview metrics/graph, process table/actions, installed applications and cache page rendered. Fixed title contrast, dark combo styling and window sizing/scrolling discovered during these checks. Read-only inventory exposed a GUID-named SystemApps package; added a location-based protection rule and regression tests, then verified that its uninstall button was disabled. Light theme rendered and persisted across closing/reopening the published app. A read-only Chrome cache scan completed at the configured 20,000-file cap; no cache was deleted.

UI deletion/uninstall of real applications was intentionally not exercised. Destructive integration tests use disposable fixtures. No Windows security/UAC prompts were automated.

## Performance

Use the read-only aggregate tool while RamMx is running:

```powershell
dotnet run --project tools/RamMx.Diagnostics -c Release -- <RamMx-PID> 120
```

The tool records normalized CPU, private working set/commit, handles and sampler duration without exporting application names or paths. Measure with a quiet system and report whether the main window is visible or in the tray. A short measurement does not establish absence of leaks.

An initial 120-second visible-window measurement found 4.34% average normalized CPU, 54.42 MiB peak private working set and 97.14 MiB peak private commit. This triggered polling/indexing and bulk collection-update improvements; it is not the final performance claim.

## Remaining production gates

- Real MSI and MSIX uninstall fixtures, cancellation, child uninstallers, reboot-required and package-data-loss semantics in disposable VMs.
- Elevated broker UAC approval/denial, different security contexts, tampering/replay and protected installation ACL validation.
- Actual Defender provider availability, scanning/remediation, policy variations and cancellation.
- Additional junction/reparse races, disk-full/IO failure, interrupted mutation and recovery scenarios.
- Vendor/version-specific cache layouts and more protected-data fixtures.
- Twenty-four-hour soak, multiple DPI settings, high contrast, screen readers and additional Windows builds.
- Publisher code signing and a signed installer/update mechanism.

The presence of a button or adapter does not mean every external provider flow passed these gates. Native uninstall is not reversible, and cache cleanup covers five explicit recipes rather than all leftovers of all software.
