# Paused checkpoint — 2026-10-27

The user requested a checkpoint and stop. This is a saved work state, not a claim of production readiness.

**Historical record:** the user resumed on 2026-10-27. The background-review feature and follow-up fixes now have 72 passing local tests. Current results superseding the pending items below are in [TESTING.md](TESTING.md); this file retains the earlier checkpoint for traceability.

## Verified baseline

- `main` commit `84dab564d9c6313f9435822785dc051859518391`: native WPF app, telemetry, guarded close/force-stop, MSI/EXE/MSIX adapters, five cache recipes, signature/Defender integration, recommendations, SQLite settings and history.
- Release build: zero warnings/errors. Local automated tests: **53 passed**, zero failed/skipped.
- Windows CI succeeded: https://github.com/sanchatmd-dev/RamMx/actions/runs/40389055065
- Published package: `artifacts/RamMx/RamMx.exe` and `artifacts/RamMx-win-x64.zip`. These binaries predate the additional background-review feature below.
- Native UI checks covered overview/processes/installed apps/cache preview, Windows SystemApps uninstall disabled, dark/light themes and settings surviving restart. No real user app was terminated/uninstalled and no real cache was deleted.
- Read-only real cache preview completed at the 20,000-file cap.
- Public README, Context, design/testing documentation and CI are present. Machine-specific research and diagnostics remain ignored.

## Changes saved at this checkpoint

- Added `BackgroundReview` and a dedicated review-candidates tab. It lists background processes with unknown paths, temporary paths or observed RAM growth, with reasons; it does not issue malware verdicts or stop anything automatically.
- Added four regression tests in `BackgroundReviewTests.cs`.
- Latest source **compiled successfully with zero warnings/errors** after these changes.
- These four new tests have **not been executed**. The new tab has **not been UI-tested**. The new source has **not been republished**. Do not report 57 tests passing.

## Performance investigation

Sampler timings are roughly 25–57 ms in recent runs. A fresh visible process, before the new review feature, measured over 120 seconds: 3.52% average normalized CPU, 52.53 MiB peak private working set, 90.90 MiB peak private commit; handles 860→871. Other instrumented runs measured approximately 4.9–7.2% CPU. These are short, non-controlled measurements on an active workstation, not a release performance guarantee.

Sampled stacks showed substantial Windows UI Automation traffic. Resetting the UI driver did not fully isolate the source of CPU use. The cause remains unresolved; do not claim it is entirely instrumentation overhead. Incremental process row updates and indexed telemetry lookup are implemented, but low-overhead and long-soak performance gates remain open.

## Resume sequence

1. Inspect branch/status and preserve any subsequent user changes.
2. Run the updated test suite (expected total 57 only if all four new tests are discovered); fix failures.
3. Test the new background-review tab and selected-item inspection flow.
4. Continue CPU investigation in an isolated environment; measure normal visible and tray use without UI automation attached. Do not disable accessibility as a workaround.
5. Close the previous test instance before publishing to its folder; rebuild/publish and verify ZIP contents, docs and executable correspond to the same source.
6. Update `docs/TESTING.md` with actual new results; merge checkpoint work to main only after verification and check CI.

Still unverified: real MSI/MSIX mutation scenarios, UAC/broker security contexts, actual Defender remediation, additional filesystem races, long soak and broader accessibility/DPI support. The preview is unsigned. Full-uninstall cleanup covers explicit supported caches and cannot promise removal of every vendor's leftovers.

An attempted cleanup of the redundant `artifacts/RamMx-verified` build directory was blocked by automatic policy review. That directory remains; it is not the current delivery folder. No bypass was attempted.
