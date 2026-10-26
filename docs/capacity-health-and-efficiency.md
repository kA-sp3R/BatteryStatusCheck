# Capacity health and background efficiency

Historical capacity/efficiency snapshot for **1.2.0**, now included in [published v1.2.0](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.2.0). Tested source: `fb41c9e63ce24cf68dbf63d6add4503597bc474c`. The [fresh release/failure report](release-1.2.0-verification.md) records final publication; results below remain tied to this earlier source. The [source/run/hash record](../.github/reviews/1.2.0.json) and [proposed release notes](../.github/reviews/1.2.0.md) are saved for review. Windows compilation, software tests, isolated runtime smoke, installer lifecycle and physical-device results are tracked separately.

## Completed behavior

- Open compact device details through the device icon in the popup. Design capacity records its value, mAh/Wh units, battery-pack/cell/load-output basis, source and entry date. Manual design entry does not create a full-charge measurement.
- The independent capacity ledger accepts a supported full-charge-capacity field or integrates measured discharge current/power across documented full/empty boundaries. It never derives capacity from charge percentage, category, voltage alone, elapsed runtime alone or assumed consumption. Remaining capacity is explicitly excluded from full-charge capacity.
- Health is usable full-charge capacity divided by design capacity, multiplied by 100, with matching units and measurement basis. Values above 100% and genuine supported zero are not silently clamped. Invalid inputs or numeric overflow leave the comparison unavailable.
- Measurement history records observation dates, method/source, sample count, observed duration, coverage, interruptions and verified battery identity. Integration requires an explicit supported sample-gap contract; a source-provided sequence must remain contiguous. Charging, missing telemetry, excessive sample gaps, dropped sequence samples, identity/session changes, clock discontinuity, sleep and interrupted restarts reject partial cycles. An active partial measurement never becomes full capacity by extrapolation.
- Configuration and bounded valid/rejected history survive restarts in `capacity-health.json` beside existing settings (64 device records, 20 history entries per device, a 2 MiB primary-file bound, fixed backup/corrupt paths). Atomic saves retain a backup; an interrupted cycle is recorded as incomplete. Capacity-store errors stay separate from genuine battery readings. Logs do not include this store or user-entered capacity sources.
- Background monitoring caches HID discovery for up to five minutes, invalidates it after coalesced Windows device-change events, and forces fresh discovery/retry after manual refresh or resume. Queries remain serialized with manual polling-rate operations.
- Unsuccessful receivers retry with a 30-second initial delay and a 60-second cap, evaluated on scheduled polling ticks. Healthy receivers retain their configured cadence. USB notifications/manual refresh bypass backoff; a radio-only reconnection without a Windows event is detected at the next eligible polling tick. Longer user refresh intervals remain authoritative.
- Battery and presentation timers stop during sleep; active battery/manual operations share power cancellation. Resume invalidates old readings and requests a fresh refresh. Generation checks prevent stale queued power events from overriding a later suspend/resume.
- Unchanged popup rows and details reuse their presentation. Tray selection skips unchanged inputs until a freshness transition, while its existing bitmap/menu caches remain intact. One taskbar DPI probe covers all icons; explicit DPI changes still update immediately. Freshness remains independent of reduced polling, and original sample times never advance during backoff.

## Evidence-backed physical health support

**No currently admitted physical model has sufficient capacity telemetry for a health result.** Razer, Logitech and Alienware devices show **Capacity measurement unavailable**, with the missing full-capacity/current/power/boundary/identity information. User-entered design capacity alone does not resolve this limit.

| Family | Existing useful battery data | Missing for capacity health |
| --- | --- | --- |
| Razer | Charge level, charging and model/link association | Supported full-capacity/current/power fields and individual battery continuity |
| Logitech | Reported SOC or native category; some voltage/status features | Supported full-capacity/current/power and complete measured discharge boundaries; a device serial alone does not identify a replacement battery |
| Alienware Pro | Charge level, charging and reported model/link | Supported capacity/current/power, full/empty boundaries and unique battery identity |

The [protocol research](research/capacity-health.md) pins sources and distinguishes absent admitted evidence from a claim that firmware has no additional features. No new experimental HID command or manufacturer health API was added.

## Verification and measurements

All final Windows checks passed at the exact source above. No local .NET compilation or physical HID access occurred.

| Verification | Actual result | Evidence |
| --- | --- | --- |
| Windows WPF compilation | Passed; no application launch | [Build 40176230540](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40176230540) |
| Focused C# software tests | 1019 passed; zero failed/skipped | Same build, actual TRX counters |
| Full solution software tests | 1019 passed in Debug and 1019 in Release; zero failed/skipped | [Full 40176230772](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40176230772); downloaded TRX and JSON agree on source/counts/native exit 0 |
| New capacity / efficiency cases | 68 capacity and 30 monitor cases passed in each configuration | Included in the 1019; existing 921 cases remain |
| Offline parser / release policy | 5 / 18 passed | Downloaded full-run logs confirm native exit 0; synthetic captures/fake GitHub |
| Portable inspections | Both passed | 11 framework-dependent files; 482 self-contained files with .NET/Windows Desktop 10.0.13; downloaded ZIP checksums, every payload hash, source/run metadata and x64 GUI PE independently checked |
| Isolated application runtime | 12 checks passed for each portable flavor | Actual packaged WPF process, synthetic data; normal controller/HID/network/startup/preferences bypassed; publication validators accepted actual reports with expected count 12 |
| Installer lifecycle | 64 unique checks passed | Install/upgrade/reinstall/two uninstall cycles; saved preferences/logs/capacity history/backup preserved, owned/unowned cleanup and mutex refusal checked; no app launch. Expected blocked-install exit 1, successful operations exit 0 |
| Visual review | Passed for produced examples | Genuine 0%, category/stale/unsupported popup, existing settings, capacity design/full/health/date fields and unavailable explanation inspected. Content below the compact details viewport is scrollable |
| Physical models / normal monitor / endurance | Not verified; no evidence-backed physical health model admitted | Boundaries below; no hardware queries or endurance work performed |

The durable ledger was loaded by new service instances after disk writes; an interrupted-process marker was tested by copying the pending ledger and reopening it. This verifies storage/restart handling, not an actual forced termination of the normal monitoring app. Installer upgrade still uses current app bytes with older installer metadata.

The reproducible workload uses two fake receivers, forty scheduled refreshes at simulated thirty-second intervals, three warmup trials and two hundred measured trials per policy. The legacy comparison uses the same managed monitor with the new efficiency policy omitted. It reports enumeration, transport/provider/query counts, process CPU time, allocations, working/private/managed memory, process/GDI/USER handles and GC counters. Time advances without sleeping. This is a short software workload, not a twenty-minute physical run or an endurance test. CPU and memory snapshots include runtime/WPF/GC/scheduler noise; deterministic operation counts provide stronger evidence than a single timing comparison.

The existing eight WPF smoke checks are retained. Added checks cover current/power integration, full versus remaining capacity, rejected interruptions, durable restart history, actual details rendering/design entry, unchanged presentation, synthetic PnP event coalescing/disposal and the efficiency workload. Installer smoke additionally preserves capacity history and its backup through install, upgrade and both uninstall cycles.

## Measured synthetic efficiency

Both portable flavors produced identical deterministic operation counts over 200 measured trials (8000 scheduled refreshes per policy):

| Operation | Legacy policy | Optimized policy | Change |
| --- | --- | --- | --- |
| Discovery enumerations | 8000 | 800 | 90% fewer |
| Transport opens / feature queries | 16000 | 12200 | 23.75% fewer |
| Healthy receiver reads | 8000 | 8000 | Cadence preserved |
| Non-responsive receiver attempts | 8000 | 4200 | 47.5% fewer |
| Queries per simulated minute | 4 | 3.05 | Same two-receiver input trace |

The following are exact short process-wide observations, with memory rounded to MiB. Arrows inside a cell show before→after within that policy run; policies run sequentially on each Windows runner, so final footprint is not a controlled comparison.

| Flavor / policy | CPU ms | Allocated MiB | Working set MiB | Private MiB | Managed heap MiB | Process handles | GDI / USER |
| --- | --- | --- | --- | --- | --- | --- | --- |
| Framework-dependent / legacy | 187.5 | 47.22 | 147.57→150.01 | 61.24→63.55 | 9.95→10.60 | 518→518 | 46→40 / 31→31 |
| Framework-dependent / optimized | 125 | 24.16 | 150.02→150.11 | 63.56→63.65 | 10.60→10.83 | 518→518 | 40→40 / 31→31 |
| Self-contained / legacy | 93.75 | 47.22 | 135.77→142.88 | 51.87→58.73 | 9.95→10.59 | 524→526 | 46→40 / 33→33 |
| Self-contained / optimized | 62.5 | 24.16 | 142.88→142.96 | 58.73→58.73 | 10.60→18.83 | 526→526 | 40→40 / 33→33 |

CPU samples were 187.5→125 ms (framework-dependent) and 93.75→62.5 ms (self-contained). Allocations were 49,511,248→25,328,960 bytes in both. These observations support reduced managed work in this fixture, not a claimed physical CPU percentage or lower steady-state memory. Managed heap growth in the self-contained optimized sample illustrates GC/order effects; it is reported rather than hidden. Raw CPU/memory/GC/handle observations are preserved in the review JSON and each `efficiency-workload.json`.

Over 200 identical presentation updates, popup/details projection counts stayed 1→1, tray selection reconciliations stayed 1→1, and no additional icon render/assignment, tooltip assignment or menu creation occurred. Alias-only changes updated a tooltip without an icon render; changed percentages, freshness and DPI invalidated the appropriate display. Twenty synthetic PnP requests coalesced into one callback; disposal prevented a pending callback. Separate 20-cycle tray resource checks returned GDI 40→40 and USER 31→31 in both packages. The previous release already cached icon bitmaps/menus; those preserved behaviors are not claimed as new savings.

## Failures found and corrected

The initial Windows run at source `d53a1732a8e4698a5471df4a8833f78abdd6a427` stopped at compilation because the new monitoring test file omitted `using Xunit;`. The import was added. Its skipped smoke jobs are not passing results; both corrected-source workflows above passed in full.

Review also fixed the queued power-event generation race, native-cancellation cleanup ownership, overflow/underflow handling, zero-rate support, missing cadence/sequence contracts, stale identity rebinding and capacity-only data with unavailable SOC. Regression cases cover the managed capacity/monitor conditions; actual Windows power events remain unverified.

## Existing functions and remaining boundaries

Single and selected-device tray modes, circular centred percentage rendering, native Logitech categories, zero values, original timestamps, notifications, aliases/preferences, installation/update options and deliberate manual polling controls remain in scope. New details actions use the existing popup; battery providers remain read-only. Source-aware collection is passive and introduces no service or additional device queries.

Normal live-monitoring startup, real Windows suspend/resume/PnP and Explorer restarts, mixed-monitor DPI, physical receiver/firmware combinations, battery accuracy, vendor-software coexistence, live update delivery, and extended performance/endurance remain unverified. Synthetic notifications and clocks do not establish those results. Installer upgrade uses the current app bytes with older installer metadata. The private repository still prevents anonymous in-app updates; its signed-in browser fallback remains available. Published packages remain unsigned.

## Functions affected

| Area | Interaction / practical limit |
| --- | --- |
| Battery refresh | Production opts into cached discovery, coalescing and backoff; manual/PnP/resume requests force fresh work. Healthy providers retain normal cadence. A failed receiver can take longer to be retried; manual refresh bypasses that delay |
| Battery presentation / notifications | Freshness stays independent. Unchanged immutable inputs skip duplicate policy/projection work; fresh sample timestamps still trigger evaluation. Category and zero behavior remain unchanged |
| Manual polling controls | Continue sharing the HID gate. Sleep cancels permission/operations; cancellation ownership lasts through native cleanup. A manual operation conservatively interrupts an active capacity cycle |
| Details / capacity store | Adds user design editing, provenance, independent calculation and bounded durable history; uncertain identities never attach saved measurements. Storage errors do not invalidate valid battery readings |
| Tray / Windows integration | Cached selections and one shared DPI probe preserve modes, notifications, explicit DPI updates, shell recovery and disposal. Real OS PnP/power/Explorer behavior is still unverified |
| Installation / updates | Capacity history lives beside preferences and survives lifecycle checks. Both portable flavors and per-user installer remain available; no update policy or deliberate-install requirement changed |

## Files changed

27 files relative to published 1.1.0 documentation/evidence HEAD. Documentation-only changes after the tested source do not change the candidate binaries.

- [.github/reviews/1.2.0.json](../.github/reviews/1.2.0.json)
- [.github/reviews/1.2.0.md](../.github/reviews/1.2.0.md)
- [.github/scripts/run-application-smoke.ps1](../.github/scripts/run-application-smoke.ps1)
- [.github/scripts/test-installer.ps1](../.github/scripts/test-installer.ps1)
- [README.md](../README.md)
- [docs/capacity-health-and-efficiency.md](../docs/capacity-health-and-efficiency.md)
- [docs/release.md](../docs/release.md)
- [docs/research/capacity-health.md](../docs/research/capacity-health.md)
- [docs/verification.md](../docs/verification.md)
- [src/DeviceBatteryStatus.App/ApplicationController.cs](../src/DeviceBatteryStatus.App/ApplicationController.cs)
- [src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj](../src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj)
- [src/DeviceBatteryStatus.App/DeviceChangeWatcher.cs](../src/DeviceBatteryStatus.App/DeviceChangeWatcher.cs)
- [src/DeviceBatteryStatus.App/DeviceDetailsWindow.cs](../src/DeviceBatteryStatus.App/DeviceDetailsWindow.cs)
- [src/DeviceBatteryStatus.App/PopupWindow.xaml](../src/DeviceBatteryStatus.App/PopupWindow.xaml)
- [src/DeviceBatteryStatus.App/PopupWindow.xaml.cs](../src/DeviceBatteryStatus.App/PopupWindow.xaml.cs)
- [src/DeviceBatteryStatus.App/Testing/EfficiencyWorkload.cs](../src/DeviceBatteryStatus.App/Testing/EfficiencyWorkload.cs)
- [src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs](../src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs)
- [src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs](../src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs)
- [src/DeviceBatteryStatus.App/Tray/TrayIconService.cs](../src/DeviceBatteryStatus.App/Tray/TrayIconService.cs)
- [src/DeviceBatteryStatus.App/ViewModels/DeviceRowViewModel.cs](../src/DeviceBatteryStatus.App/ViewModels/DeviceRowViewModel.cs)
- [src/DeviceBatteryStatus.Core/Health/CapacityHealthService.cs](../src/DeviceBatteryStatus.Core/Health/CapacityHealthService.cs)
- [src/DeviceBatteryStatus.Core/Health/CapacityModels.cs](../src/DeviceBatteryStatus.Core/Health/CapacityModels.cs)
- [src/DeviceBatteryStatus.Core/Models/DeviceSnapshot.cs](../src/DeviceBatteryStatus.Core/Models/DeviceSnapshot.cs)
- [src/DeviceBatteryStatus.Core/Monitoring/BatteryMonitor.cs](../src/DeviceBatteryStatus.Core/Monitoring/BatteryMonitor.cs)
- [src/DeviceBatteryStatus.Core/Monitoring/BatteryMonitorOptions.cs](../src/DeviceBatteryStatus.Core/Monitoring/BatteryMonitorOptions.cs)
- [tests/DeviceBatteryStatus.Tests/BatteryMonitorEfficiencyTests.cs](../tests/DeviceBatteryStatus.Tests/BatteryMonitorEfficiencyTests.cs)
- [tests/DeviceBatteryStatus.Tests/CapacityHealthTests.cs](../tests/DeviceBatteryStatus.Tests/CapacityHealthTests.cs)
