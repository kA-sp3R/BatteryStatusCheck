# Package size and background overhead

This work optimizes the existing unpublished 1.2.0 candidate. v1.1.0 remains the published release. Distribution size, synthetic software measurements and live application performance are separate outcomes.

## Baseline and method

The baseline is the verified runtime/gaming-alert candidate at `1ca669c2b90770d7074cad4b98f156b0031a9240`, [Windows run 40233889412](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40233889412), attempt 1. Downloaded archives were inventoried without extraction or execution; their sibling checksums, manifest provenance and every payload hash were checked. Extracted size means the sum of uncompressed file bytes, excluding filesystem allocation overhead, user data and an already-installed shared framework.

| Distribution | Baseline download bytes | Baseline extracted bytes | Files |
| --- | ---: | ---: | ---: |
| Framework-dependent ZIP | 391,744 | 1,028,292 | 11 |
| Self-contained ZIP | 76,418,618 | 180,485,731 | 482 |
| Per-user installer | 54,855,102 | Uses the self-contained payload plus installer bookkeeping | — |

Both package flavors use Release/win-x64, no application ReadyToRun, no trimming/single-file publishing and no published debug symbols. The baseline SDK is 10.0.403; the self-contained package bundles .NET/Windows Desktop 10.0.14. SDK selection is now pinned to that same SDK for a comparable build. Updating the pinned SDK remains a deliberate maintenance step.

The bundled framework accounts for 179,287,344 uncompressed bytes across 468 runtime/localisation files, 99.34% of the self-contained payload. Its largest contributors include `System.Private.CoreLib.dll` (16,033,616 bytes), `PresentationFramework.dll` (15,824,680), `System.Windows.Forms.dll` (13,715,240), `PresentationCore.dll` (8,324,904) and `System.Private.Xml.dll` (7,788,328). The 221 satellite assemblies account for 17,843,472 bytes and preserve framework localisation. The six application-owned executable/configuration files total 963,395 bytes. Runtime contribution is therefore distinct from application code size; the framework-dependent package relies on a separately installed Windows Desktop runtime.

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

Windows compilation, complete hardware-free software suites, companion smoke against both actual package flavors and installer lifecycle checks are pending for the optimized source. Final size deltas, exact source/run/hash records, changed files and measured synthetic results will be added after those checks pass.

Normal app startup time, live idle CPU/working set and real hardware activity are not measured by the companion harness. Synthetic workload process observations are not evidence of a lower idle footprint. Physical devices, games, OS power/PnP/Explorer events, mixed-monitor DPI and extended endurance remain separately unverified. No tag, draft or new release has been created.
