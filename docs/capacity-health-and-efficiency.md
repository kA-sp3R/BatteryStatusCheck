# Capacity health and background efficiency

Development candidate: **1.2.0**, following published v1.1.0. Publication requires the user's decision after verification. Windows compilation, software tests, isolated runtime smoke, installer lifecycle and physical-device results are tracked separately.

## Completed behavior

- Open compact device details through the device icon in the popup. Design capacity records its value, mAh/Wh units, battery-pack/cell/load-output basis, source and entry date. Manual design entry does not create a full-charge measurement.
- The independent capacity ledger accepts a supported full-charge-capacity field or integrates measured discharge current/power across documented full/empty boundaries. It never derives capacity from charge percentage, category, voltage alone, elapsed runtime alone or assumed consumption. Remaining capacity is explicitly excluded from full-charge capacity.
- Health is usable full-charge capacity divided by design capacity, multiplied by 100, with matching units and measurement basis. Values above 100% and genuine supported zero are not silently clamped. Invalid inputs or numeric overflow leave the comparison unavailable.
- Measurement history records observation dates, method/source, sample count, observed duration, coverage, interruptions and verified battery identity. Integration requires an explicit supported sample-gap contract; a source-provided sequence must remain contiguous. Charging, missing telemetry, excessive sample gaps, dropped sequence samples, identity/session changes, clock discontinuity, sleep and interrupted restarts reject partial cycles. An active partial measurement never becomes full capacity by extrapolation.
- Configuration and bounded valid/rejected history survive restarts in `capacity-health.json` beside existing settings. Atomic saves retain a backup; an interrupted cycle is recorded as incomplete. Capacity-store errors stay separate from genuine battery readings. Logs do not include this store or user-entered capacity sources.
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

Windows compilation, complete C# suites, parser/release-policy tests, both portable inspections, twelve isolated application-smoke checks per flavor and installer lifecycle checks are pending for the final candidate source. No local .NET compilation or physical HID access has occurred.

The reproducible workload uses two fake receivers, forty scheduled refreshes at simulated thirty-second intervals, three warmup trials and two hundred measured trials per policy. The legacy comparison uses the same managed monitor with the new efficiency policy omitted. It reports enumeration, transport/provider/query counts, process CPU time, allocations, working/private/managed memory, process/GDI/USER handles and GC counters. Time advances without sleeping. This is a short software workload, not a twenty-minute physical run or an endurance test. CPU and memory snapshots include runtime/WPF/GC/scheduler noise; deterministic operation counts provide stronger evidence than a single timing comparison.

The existing eight WPF smoke checks are retained. Added checks cover current/power integration, full versus remaining capacity, rejected interruptions, durable restart history, actual details rendering/design entry, unchanged presentation, synthetic PnP event coalescing/disposal and the efficiency workload. Installer smoke additionally preserves capacity history and its backup through install, upgrade and both uninstall cycles.

## Existing functions and remaining boundaries

Single and selected-device tray modes, circular centred percentage rendering, native Logitech categories, zero values, original timestamps, notifications, aliases/preferences, installation/update options and deliberate manual polling controls remain in scope. New details actions use the existing popup; battery providers remain read-only. Source-aware collection is passive and introduces no service or additional device queries.

Normal live-monitoring startup, real Windows suspend/resume/PnP and Explorer restarts, mixed-monitor DPI, physical receiver/firmware combinations, battery accuracy, vendor-software coexistence, live update delivery, and extended performance/endurance remain unverified. Synthetic notifications and clocks do not establish those results. Installer upgrade uses the current app bytes with older installer metadata. The private repository still prevents anonymous in-app updates; its signed-in browser fallback remains available. Published packages remain unsigned.

## Files changed

The complete edited-file list, exact source/run IDs, actual test counts, measurements and any failures fixed will be recorded after Windows verification.
