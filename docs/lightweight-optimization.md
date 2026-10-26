# Package size and background overhead

These optimizations are included in [published v1.2.0](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.2.0). This report retains earlier `4612373` measurements; the [fresh release/failure report](release-1.2.0-verification.md) records the tested/published `d76cd56` build and exact assets. Distribution size, synthetic measurements and live performance remain separate outcomes.

## Baseline and method

The baseline is the verified runtime/gaming-alert candidate at `1ca669c2b90770d7074cad4b98f156b0031a9240`, [Windows run 40233889412](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40233889412), attempt 1. Downloaded archives were inventoried without extraction or execution; their sibling checksums, manifest provenance and every payload hash were checked. Extracted size means the sum of uncompressed file bytes, excluding filesystem allocation overhead, user data and an already-installed shared framework.

| Distribution | Baseline download bytes | Baseline extracted bytes | Files |
| --- | ---: | ---: | ---: |
| Framework-dependent ZIP | 391,744 | 1,028,292 | 11 |
| Self-contained ZIP | 76,418,618 | 180,485,731 | 482 |
| Per-user installer | 54,855,102 | Uses the self-contained payload plus installer bookkeeping | — |

Both package flavors use Release/win-x64, no application ReadyToRun, no trimming/single-file publishing and no published debug symbols. The baseline SDK is 10.0.403; the self-contained package bundles .NET/Windows Desktop 10.0.14. SDK selection is now pinned to that same SDK for a comparable build. Updating the pinned SDK remains a deliberate maintenance step.

The bundled framework accounts for 179,287,344 uncompressed bytes across 468 runtime/localisation files, 99.34% of the self-contained payload. Its largest contributors include `System.Private.CoreLib.dll` (16,033,616 bytes), `PresentationFramework.dll` (15,824,680), `System.Windows.Forms.dll` (13,715,240), `PresentationCore.dll` (8,324,904) and `System.Private.Xml.dll` (7,788,328). The 221 satellite assemblies account for 17,843,472 bytes and preserve framework localisation. The six application-owned executable/configuration files total 926,626 bytes in the framework-dependent package and 963,395 bytes in self-contained; the latter also has a larger runtime dependency inventory. Runtime contribution is therefore distinct from application code size; the framework-dependent package relies on a separately installed Windows Desktop runtime.

There are no third-party production NuGet package references. xUnit, its runner and Microsoft.NET.Test.Sdk remain test-only dependencies. Production framework references, runtime files, resources and licence/notice files are retained. There were no published PDB/XML/ICU files to remove.

## Implemented changes

- Move synthetic fixtures out of the production assembly into a separate CI-only WPF harness. It references and verifies the exact packaged application/Core/Windows DLLs, loads the same packaged theme and checks the actual packaged runtime for the self-contained flavor. The normal apphost/startup/controller are not launched by companion smoke. No harness or verification files enter the application ZIP or installer.
- Use `Deflate/SmallestSize` for portable ZIPs. Compression changes the download, not extracted size or steady-state CPU/memory. Installer compression stays at its existing solid LZMA2 normal setting.
- Prepare an independently owned alert-settings snapshot once per settings change. Freshness, charging, snooze, cooldown and queued-alert relevance still run on each presentation tick. Reuse the runtime-result dictionary and cached app-version string; create the main popup on first opening rather than during hidden startup. Tray runtime association uses a direct scan that preserves duplicate-ID rejection without per-icon iterator/array allocations.
- Bound new settings reads/writes and corrupt-file copies to 1 MiB, preserve oversized originals and existing valid settings/backups, and retain one latest corrupt snapshot instead of another copy on every failed startup. Only exact legacy app-owned GUID backup names are migrated.
- Bound transient capacity advisories to 64 recent unstored identities, alongside the protected 64-device persisted ledger. Rejected overflow devices no longer enter accepted-telemetry caches. Stored history, active measurements and known identity-interruption guards are retained.
- Reuse diagnostic retention scans for five minutes (or the shorter configured retention), with forced scans at startup/export/rotation and clock rollback. File-size/count bounds and redaction remain active. An expired externally present log can wait until the next scan; normal rotation remains bounded.

WPF trimming and Native AOT are not supported publishing options for this application. Invariant globalization and removing satellite assemblies would alter culture/localisation behavior. Single-file bundling would change deployment/extraction behavior without removing the required framework. These changes were not enabled.

## Verification and measurements

The optimized source is `46123738ffa203b4978bc06d8fa94e97d34c75ab`. [Windows compilation/packages/companion/installer run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40236243538), [full software run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40236244173), [reviewed sizes, counts, hashes and measured observations](../.github/reviews/1.2.0-lightweight.json), [candidate notes](../.github/reviews/1.2.0-lightweight.md). Downloaded CI inventories were independently recomputed and matched exactly.

| Distribution | Download: before → after (bytes) | Download reduction | Extracted: before → after (bytes) |
| --- | ---: | ---: | ---: |
| Framework-dependent ZIP | 391,744 → 336,205 | 55,539 / 14.18% | 1,028,292 → 888,053 |
| Self-contained ZIP | 76,418,618 → 73,783,640 | 2,634,978 / 3.45% | 180,485,731 → 180,345,492 |
| Per-user installer | 54,855,102 → 54,824,007 | 31,095 / 0.057% | See self-contained payload; installed uninstaller/bookkeeping excluded |

Each portable flavor loses 140,239 uncompressed bytes. The main app DLL falls from 349,184 to 205,312 bytes (41.2%); Core grows from 384,512 to 388,096 bytes for the bounds/preferences helpers. File counts remain 11 and 482. All 468 runtime/localisation files have identical hashes and total 179,287,344 uncompressed bytes before and after. Their combined compressed bytes fall by 2,580,909, demonstrating a download saving without removing framework functionality. Notice hashes are unchanged. Verification data, the companion executable and development symbols are excluded from normal downloads; detailed evidence remains separate.

| Verification outcome | Result |
| --- | --- |
| Windows compilation | Passed Release solution build, including the separate harness; no production app launch. |
| C# automated tests | 1,116 passed in each Debug/Release, zero failures/skips/errors/timeouts. Focused Release job also passed 1,116. New cases: 8 prepared preferences, 8 storage/retention and 5 capacity-cache continuity checks. |
| Offline software tools | 5 parser and 39 automation-policy/inventory cases passed. The latter include 17 inventory and 4 companion publication cases. Fake GitHub only. |
| Portable validation | Both ZIPs passed source/run/SDK/runtime metadata, payload/sibling checksums, x64 GUI PE and absence-of-development-files checks. |
| Companion runtime verification | 18/18 checks per flavor; exact packaged application/Core/Windows DLL hashes, shared theme, rendering, settings, updates-off, tray/DPI/disposal, capacity details/history, runtime estimates and gaming alerts. Self-contained execution verified its actual bundled coreclr/CoreLib bytes while global runtime lookup pointed to an empty directory. |
| Installer lifecycle | 64/64 assertions: install, blocked-running-app refusal, upgrade, reinstall, uninstall and preservation of preferences/logs/capacity history. No app launch. The previous-version fixture contains current app bytes and exercises lifecycle only. |

All Windows jobs passed on the first attempt; no failed check required a source fix or rerun. The new runtime-popup, gaming-alert, gaming-alert-settings and main-settings screenshots are byte-identical to the baseline and were visually reviewed. Actual native no-activation/click-through styles/hooks and foreground preservation passed on the runner; this remains distinct from physical input/game compatibility.

The prepared-settings test made 50 evaluations and 50 queued-alert revalidations after one preparation; each source dictionary was enumerated once. Storage tests exercised ten corrupt loads with one retained snapshot, five-minute diagnostic scan reuse with 100 writes, and 1,000 synthetic identities against bounded capacity caches while retaining known guards/history/active cycles. These are deterministic software work/storage counts, not measured whole-app CPU or memory reductions.

The retained monitor workload uses 200 trials of 20 simulated minutes, 30-second scheduled refreshes and two synthetic receivers, with three warmup trials per policy. It compares two monitor policies within this build; the cached policy already existed before this task. Healthy reads remain 8,000, discovery 8,000→800 and feature queries 16,000→12,200. No real twenty-minute session or hardware query was performed.

| Synthetic companion observation | Framework-dependent | Self-contained |
| --- | ---: | ---: |
| Process CPU during legacy/cached policy loops (ms) | 109.375 / 78.125 | 343.750 / 125.000 |
| Managed bytes allocated, legacy/cached loops | 49,509,944 / 25,329,096 | 49,511,256 / 25,328,968 |
| Working set, before legacy → after cached (bytes) | 144,785,408 → 219,385,856 | 143,110,144 → 154,677,248 |
| Private memory, before legacy → after cached (bytes) | 55,902,208 → 130,519,040 | 52,998,144 → 64,184,320 |
| Process handles, same endpoints | 529 → 531 | 527 → 529 |
| GDI / USER handles across 20 isolated tray lifecycle iterations | 40→40 / 32→31 | 40→40 / 30→30 |

The loops are short, sequential, process-wide observations affected by JIT, GC, scheduling and retained heap pages. Memory increased during this transient workload; it is not a peak, idle or steady-state footprint. The test host changed from embedded application fixtures to a companion process, so these observations are not a before/after product-performance comparison. No startup-time, idle CPU or idle-memory saving is claimed.

## Files, dependencies and tradeoffs

No production package dependency was added, removed or replaced. The separate harness uses the existing WPF/Windows Forms framework and actual packaged application assemblies. Existing test dependencies remain test-only. Implementation and test/packaging files changed relative to the prior candidate:

- [`.github/scripts/build-smoke-harness.ps1`](../.github/scripts/build-smoke-harness.ps1)
- [`.github/scripts/measure-package-size.py`](../.github/scripts/measure-package-size.py)
- [`.github/scripts/package-windows.ps1`](../.github/scripts/package-windows.ps1)
- [`.github/scripts/publish-verified-preview.py`](../.github/scripts/publish-verified-preview.py)
- [`.github/scripts/run-application-smoke.ps1`](../.github/scripts/run-application-smoke.ps1)
- [`.github/scripts/test_measure_package_size.py`](../.github/scripts/test_measure_package_size.py)
- [`.github/scripts/test_publish_verified_preview.py`](../.github/scripts/test_publish_verified_preview.py)
- [`.github/scripts/verify-windows-package.ps1`](../.github/scripts/verify-windows-package.ps1)
- [`.github/workflows/full-software-tests.yml`](../.github/workflows/full-software-tests.yml)
- [`.github/workflows/windows-build.yml`](../.github/workflows/windows-build.yml)
- [`DeviceBatteryStatus.sln`](../DeviceBatteryStatus.sln)
- [`global.json`](../global.json)
- [`src/DeviceBatteryStatus.App/App.xaml`](../src/DeviceBatteryStatus.App/App.xaml)
- [`src/DeviceBatteryStatus.App/App.xaml.cs`](../src/DeviceBatteryStatus.App/App.xaml.cs)
- [`src/DeviceBatteryStatus.App/ApplicationController.cs`](../src/DeviceBatteryStatus.App/ApplicationController.cs)
- [`src/DeviceBatteryStatus.App/Properties/AssemblyInfo.cs`](../src/DeviceBatteryStatus.App/Properties/AssemblyInfo.cs)
- [`src/DeviceBatteryStatus.App/Themes/Controls.xaml`](../src/DeviceBatteryStatus.App/Themes/Controls.xaml)
- [`src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs`](../src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs)
- [`src/DeviceBatteryStatus.Core/Alerts/GamingAlertPolicy.cs`](../src/DeviceBatteryStatus.Core/Alerts/GamingAlertPolicy.cs)
- [`src/DeviceBatteryStatus.Core/Alerts/GamingAlertPreferences.cs`](../src/DeviceBatteryStatus.Core/Alerts/GamingAlertPreferences.cs)
- [`src/DeviceBatteryStatus.Core/Diagnostics/DiagnosticLog.cs`](../src/DeviceBatteryStatus.Core/Diagnostics/DiagnosticLog.cs)
- [`src/DeviceBatteryStatus.Core/Health/CapacityHealthService.cs`](../src/DeviceBatteryStatus.Core/Health/CapacityHealthService.cs)
- [`src/DeviceBatteryStatus.Core/Settings/FileSettingsStore.cs`](../src/DeviceBatteryStatus.Core/Settings/FileSettingsStore.cs)
- [`tests/DeviceBatteryStatus.Smoke/DeviceBatteryStatus.Smoke.csproj`](../tests/DeviceBatteryStatus.Smoke/DeviceBatteryStatus.Smoke.csproj)
- [`tests/DeviceBatteryStatus.Smoke/Program.cs`](../tests/DeviceBatteryStatus.Smoke/Program.cs)
- [`tests/DeviceBatteryStatus.Smoke/SmokeProvenance.cs`](../tests/DeviceBatteryStatus.Smoke/SmokeProvenance.cs)
- `src/DeviceBatteryStatus.App/Testing/EfficiencyWorkload.cs` → [`tests/DeviceBatteryStatus.Smoke/Testing/EfficiencyWorkload.cs`](../tests/DeviceBatteryStatus.Smoke/Testing/EfficiencyWorkload.cs)
- `src/DeviceBatteryStatus.App/Testing/GamingAlertSmoke.cs` → [`tests/DeviceBatteryStatus.Smoke/Testing/GamingAlertSmoke.cs`](../tests/DeviceBatteryStatus.Smoke/Testing/GamingAlertSmoke.cs)
- `src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs` → [`tests/DeviceBatteryStatus.Smoke/Testing/SoftwareSmokeTest.cs`](../tests/DeviceBatteryStatus.Smoke/Testing/SoftwareSmokeTest.cs)
- [`tests/DeviceBatteryStatus.Tests/CapacityCacheBoundsTests.cs`](../tests/DeviceBatteryStatus.Tests/CapacityCacheBoundsTests.cs)
- [`tests/DeviceBatteryStatus.Tests/GamingAlertPreferencesTests.cs`](../tests/DeviceBatteryStatus.Tests/GamingAlertPreferencesTests.cs)
- [`tests/DeviceBatteryStatus.Tests/StorageBoundsTests.cs`](../tests/DeviceBatteryStatus.Tests/StorageBoundsTests.cs)

The packaging/runtime harness and storage/alert/popup paths are affected; battery protocol commands, sampling frequency, refresh coordination, capacity integration/health arithmetic, installer policy, update verification and deliberate manual polling-rate operations retain their implementations and regression coverage. Lazy popup wiring received static review; its normal production startup path was not exercised by companion smoke.

Tradeoffs: stronger ZIP compression can require more packaging CPU; compression CPU was not benchmarked. Compression leaves extracted bytes unchanged; retaining the full desktop runtime/localisation limits the self-contained reduction. Verification now needs a separate harness build and explicit assembly/runtime provenance. One corrupt snapshot replaces indefinite forensic copies, and oversized settings return an honest error while preserving originals. Diagnostic expiry scans can wait up to five minutes; size/count controls remain. Unknown health advisories may age out after 64 newer unstored identities; stored measurements and known identity protections remain.

Full software testing was necessary for these changes and completed. Normal app startup time, live idle CPU/working set and real hardware activity were not measured. Physical devices, real-game/input routing, actual OS power/PnP/Explorer events, physical mixed-DPI behavior and extended endurance remain separately unverified. No currently admitted physical model gained capacity-health telemetry. The prior runtime/gaming review is marked superseded, and this report/review/README/verification-only follow-up does not change the tested binaries. No tag/draft/release was created at this earlier snapshot. The user subsequently authorised fresh full testing and [v1.2.0 publication](release-1.2.0-verification.md); its asset metadata differs from these historical measurements.
