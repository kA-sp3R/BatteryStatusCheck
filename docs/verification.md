# Verification

Milestone 4 review, the Razer HyperPolling/high-rate fix and the Logitech category follow-up passed Windows compilation, focused regressions, the full existing automated software suite and both preview.3 package inspections for the source recorded below. Application runtime and physical hardware remain separate and unperformed.

| Category | Status | Evidence / next action |
| --- | --- | --- |
| Static review | Completed | Source/peer review, XML/project-reference checks and whitespace checks. This is not compilation or a rendered UI check. |
| Windows compilation | Passed | [Windows run `39527777290`](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39527777290), source commit `b53b6b777d409f83d081b51a7a3be4f06c1aa45d`; WPF solution compiled and development artifact uploaded. |
| Focused C# automated tests | 451 passed; 0 failed; 0 skipped | Same Windows run/source commit. Actual TRX counters exposed through check `118986361241`: Executed=451, Passed=451, Failed=0, Skipped=0, NativeExit=0. Downloaded TRX confirms all 451 cases passed, including all 70 category presentation/decoder cases, provider fallback/capability failures, stale retention and prior battery, polling, settings and presentation regressions. |
| Offline capture parser tests | 5 passed; 0 skipped | Same Windows run/source commit, check `118986361586`: Ran=5, Skipped=0, NativeExit=0. Synthetic USBPcap submissions/completions, IRP reuse, zero bytes/percentage fields, filtering and truncation. This does not validate actual capture files or hardware. |
| Framework-dependent package | Passed static inspection | Check `118986813651`: 11 files, x64 GUI PE, runtime metadata, source/run provenance and all payload hashes verified. No application launch. |
| Self-contained package | Passed static inspection | Check `118986813634`: 482 files, x64 GUI PE, bundled .NET/Windows Desktop 10.0.13, license provenance and all payload hashes verified. No application launch. |
| GitHub prerelease | Published | [v0.1.0-preview.3](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v0.1.0-preview.3), tag target `b53b6b777d409f83d081b51a7a3be4f06c1aa45d`. [Publication run `39529174142`](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39529174142) verified all 6 uploaded asset sizes/digests before publishing; release/tag and downloaded verification evidence were independently checked afterward. Packages are unsigned. |
| Full existing automated software suite | Passed | User authorized full testing and publication. [Windows run `39528608946`](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39528608946) tested application source `b53b6b777d409f83d081b51a7a3be4f06c1aa45d` with automation `6fa0076`. Complete solution: 451 C# tests passed in Debug and 451 in Release; all 5 offline parser tests passed. No filters, failures or skips. Reports downloaded and independently checked. |
| Application runtime | Not run | Requires explicit permission on Windows. |
| Physical hardware | Not run | Requires explicit permission; no compatibility or accuracy certification claimed. |

The app does not start during CI. Hardware tests are not part of automated tests.

Live GitHub API requests work. Current requests establish API connectivity; environment metadata alone does not establish whether an operation succeeds. CI exposes actual test counters and failure messages as check annotations through the API; full logs/TRX files are also retained as artifacts. No Git-note writer or extra repository-write permission is needed.

Malformed Razer/Alienware replies produce failed readings. A valid Razer battery measurement survives an invalid charging reply with charging left unknown. These earlier regressions remain covered in the passing source commit above. Documentation-only status commits use `[skip ci]` and do not change that tested application source.

## Milestone 2 implementation

- One DPI-aware circular tray icon; fresh lowest/preferred-device selection, exact percentage colors and neutral unavailable state.
- Compact black/green popup with dynamic height, device glyphs, independent charging/link labels, stale timestamps, keyboard/outside dismissal and monitor work-area placement.
- One serialized background polling loop, independent presentation expiry, refresh after resume and cancellation before exit.
- Refresh interval, aliases, preferred device, low-battery alerts and explicit opt-in Windows startup; settings saves retain a recoverable prior file and do not overwrite unrelated startup commands.
- Focused hardware-free tests cover real zero, stale/preferred selection, alias identity preservation, alert suppression/recovery, settings round trips and corrupt-file preservation.

## Outstanding limitations

- Alienware Pro now has an implementation based on the exact-model MIT project `romain-ncls/awpro`, revision `d92f6df5`, whose documented frames and fixtures come from AWCC captures. This establishes implementation evidence, not local Windows or hardware validation. The original pcaps/descriptors are not public in that repository. See [research](research/alienware-awcc.md).
- Generic Razer HyperPolling battery/paired-model association and shared multi-peripheral association are unsupported. Its manual controller addresses receiver configuration with the paired model/link explicitly unknown. Razer battery replies do not independently prove a live radio connection or exclude firmware-cached data.
- Logitech support is constrained to documented receiver IDs and battery features. Category-only models now display their reported category without an estimated percentage; exact percentage availability depends on device-advertised capabilities. No particular device's feature set has been verified on hardware.
- Windows discovery and native I/O, protocol compatibility, reconnect behavior, and firmware caching need application/hardware verification after permission. Read-only query packets still access hardware when the user launches the app.
- Milestone 4 was approved by the user. The interface, shell notification delivery, mixed-DPI placement, startup registration and full shutdown behavior remain unverified at runtime.

## Milestone 3 implementation

- Separate polling-rate window, disabled each time it opens. Detect reads capabilities; only an explicit Apply can change a setting. No automatic restore or persisted rate is used.
- Exact mapped ordinary Razer wireless receiver IDs use the documented 125/500/1000 Hz protocol. The subsequent high-rate fix adds `009F`, `00C1`, `00BF` and generic HyperPolling `00B3` with source-profile options through 8000 Hz; generic receiver configuration does not establish paired-mouse identity or battery support.
- Logitech HID++ 8060/8061 offers device-advertised mouse rates, potentially through 8000 Hz, after fresh identity checks. Unknown identity, unsupported versions and profile-controlled modes are refused.
- A shared HID gate serializes battery and manual exchanges. Each Apply checks its target/options and sends the documented setting sequence without retries. Razer high-rate configuration requires two commands, with cancellation and opt-in checked before each; uncertain or partial outcomes remain unverified. Fresh readback confirms the reported effective configuration, not independently stored state for each phase.
- Disable, selection changes, window closure, suspend/resume and exit revoke pending manual operations. Polling failures do not alter battery data.

Configured-rate readback is not a measurement of USB report timing. No polling-rate behavior, firmware compatibility or receiver/peripheral pairing has been verified on hardware.

## Milestone 4 review and release readiness

- Reviewed Core battery/polling models and protocols, settings/presentation, WPF/tray lifecycle and native Windows discovery/I/O/startup. Native lifetime and GDI ownership review found no material defect; this does not establish runtime behavior.
- Preserved fresh link evidence when a failed battery read retains a stale percentage; cancellation after discovery/reads or permission callbacks cannot publish late results or start manual operations.
- Gated Logitech `1000` percentages on its advertised mileage capability. The category follow-up below replaces the earlier unavailable category-only presentation. Razer admission excludes unknown/wired products while explicitly recognizing the HyperPolling receiver with unavailable battery data.
- Removed unused tray anchoring code and prevented nonpositive HID++ read deadlines. No CPU/memory or real-time performance measurements were taken.
- Added unsigned `win-x64` framework-dependent and self-contained preview packages, provenance manifests, notices, archive/payload checksums and static ZIP/PE/runtime inspection. See [release instructions](release.md). The reviewed CI packages are now published as prerelease assets.

Source review, Windows compilation, focused regressions, the full existing automated suite and both package inspections passed. Application runtime and physical hardware verification remain unperformed. Hardware compatibility, reconnect behavior, notifications, visual correctness, startup behavior and actual polling frequency remain open validation work. A fully verified release is not claimed.

## Full automated software suite

The manual **Full software suite (no application or hardware launch)** workflow accepts an exact application source SHA. It tests the complete solution without filters in Debug and Release on Windows, checks that every test project is included and produces a TRX report, rejects missing/skipped/failed tests, and runs every offline parser test. Compilation and packaging retain their separate Windows workflow evidence. Current automated tests cover Core and synthetic parser files; there are no application runtime or physical hardware tests in the suite.

The passing run above executed 902 C# test cases across the two configurations (451 distinct cases per configuration) plus 5 parser cases. Checks `118989029849` (Debug) and `118989029451` (Release) report Executed=451, Passed=451, Failed=0, Skipped=0, NativeExit=0 for the exact application source. Parser check `118989029698` reports Ran=5, Skipped=0, NativeExit=0. Downloaded TRX/JSON/log files agree with those counters. An initial automation-only run failed before C# execution due to a PowerShell variable-name collision; the corrected runner produced the passing evidence.

## Logitech category follow-up

- Model and monitor retain a device-reported percentage or category, never an estimated percentage. Categories follow the same freshness rules and retain their original timestamps when stale; confirmed disconnection clears them.
- Popup rows display the native category with no percentage bar. Tray tooltips retain categories while the icon stays neutral. Preferred devices stay selected; percentage alerts and numeric tray ranking ignore categories.
- HID++ `1004` categories are capability-masked. A category-only response triggers discovery of `1000`; only valid mileage capabilities authorize a percentage. Unsupported, malformed and failed optional queries retain the original category and report the fallback outcome. Timeouts stop the HID session; explicit disconnection invalidates the category.
- The primary Logitech 2012-06-04 specification defines `1000` categories as Critical/Low/Good/Full, including encoded `25` as Low and `50` as Good. It also defines level zero as unknown, even in mileage mode. Genuine `0%` from `1004` SOC and legacy `0D` remains valid. See [protocol evidence](protocols/logitech.md).
- Legacy `0D` is preferred over category register `07`. Voltage `1001` and ADC `1F20` require host estimation and are excluded as exact percentage alternatives. Centurion `0104` belongs to a separate headset transport and is not implemented here.
- Focused regressions cover native category boundaries, genuine zero versus unknown sentinels, capability gating, alternative percentage/category selection, fallback failure/disconnection/timeout handling, stale retention, tray text and percentage-only alerts. These tests use scripted transports only.

## Preview downloads

Both packages are version `0.1.0-preview.3`, built with SDK `10.0.402` from source `b53b6b7` in the Windows run above and published under that exact source tag. Both packages were downloaded successfully; local archive checksums, every payload hash and embedded source/run provenance match the Windows evidence and published asset digests. Release assets below have no extra wrapper. Actions artifacts remain available separately with their 14-day retention and wrapper ZIP.

| Package | ZIP size | SHA-256 | Release asset |
| --- | --- | --- | --- |
| Framework-dependent; needs .NET 10 Windows Desktop Runtime x64 | 206,233 bytes | `3b7a7f639cae65c65ab8ea9b4e53c7ecf18c2a1f6596d69d86266a9147c78fa5` | [Download](https://github.com/kA-sp3R/BatteryStatusCheck/releases/download/v0.1.0-preview.3/DeviceBatteryStatus-0.1.0-preview.3-win-x64-framework-dependent.zip) |
| Self-contained | 76,232,865 bytes | `1856b00f85961779429d120a7e92de18fd04c2605290db2d6157ac12ef0ddff1` | [Download](https://github.com/kA-sp3R/BatteryStatusCheck/releases/download/v0.1.0-preview.3/DeviceBatteryStatus-0.1.0-preview.3-win-x64-self-contained.zip) |
| Full test evidence and provenance | 222,371 bytes | `f51a8640d2e7ee2e6d729d86dd419e07a2c677fa0501e73b04679f8f88db7aa8` | [Download](https://github.com/kA-sp3R/BatteryStatusCheck/releases/download/v0.1.0-preview.3/DeviceBatteryStatus-0.1.0-preview.3-verification.zip) |

The local cloud environment's release upload host returned HTTP 401, while the release API remained authorized. Publication therefore used the repository-scoped GitHub Actions token with `actions:read` and `contents:write` in the manual publication job. The successful job checked source/test provenance and uploaded file hashes before publishing; the release receipt and test evidence are retained as release assets and workflow evidence.

The Razer fix adds documented two-command high-rate configuration for `009F`, `00C1`, `00BF` and generic HyperPolling `00B3`. Each command has a fresh cancellation/opt-in check; failed or partial sequences are unverified and never retried automatically. Detect followed by explicit same-rate Apply can complete a later deliberate attempt even if the first phase already affected the reported rate. Generic receiver configuration leaves paired-mouse identity, radio link and battery unavailable. Static source profiles do not establish negotiated firmware capabilities or physical polling frequency; see [protocol evidence](protocols/razer-polling.md).

The earlier packaging failure was confined to ZIP timestamp assignment and missing runtime-pack notice text. Windows CI confirmed both fixes in this passing run: timestamps are set before writing entries, and absent pack notices explicitly refer to bundled distribution-wide notices while retaining exact-pack licenses.
