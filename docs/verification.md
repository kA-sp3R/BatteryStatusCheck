# Milestones 1–4 verification

Milestone 4 source review, Windows compilation, focused regressions and both preview package inspections passed for the source recorded below. A subsequent Razer HyperPolling/high-rate fix is implemented and awaiting Windows CI; the table still records the previous baseline. Application runtime, physical hardware and full testing remain separate and unperformed.

| Category | Status | Evidence / next action |
| --- | --- | --- |
| Static review | Completed | Source/peer review, XML/project-reference checks and whitespace checks. This is not compilation or a rendered UI check. |
| Windows compilation | Passed | [Windows run `39419305858`](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39419305858), source commit `8bf6aeb005b7e805d2955b95abbf9a593bab1b0e`; WPF solution compiled and development artifact uploaded. |
| Focused C# automated tests | 291 passed; 0 failed; 0 skipped | Same Windows run/source commit. Actual TRX counters exposed through check `118615974334`: Executed=291, Passed=291, Failed=0, Skipped=0, NativeExit=0. Includes 14 new Milestone 4 accuracy/cancellation regressions alongside prior protocol, presentation and settings cases. |
| Offline capture parser tests | 5 passed; 0 skipped | Same Windows run/source commit, check `118615974087`: Ran=5, Skipped=0, NativeExit=0. Synthetic USBPcap submissions/completions, IRP reuse, zero bytes/percentage fields, filtering and truncation. This does not validate actual capture files or hardware. |
| Framework-dependent package | Passed static inspection | Check `118616469454`: 11 files, x64 GUI PE, runtime metadata, source/run provenance and all payload hashes verified. No application launch. |
| Self-contained package | Passed static inspection | Check `118616469483`: 482 files, x64 GUI PE, bundled .NET/Windows Desktop 10.0.13, license provenance and all payload hashes verified. No application launch. |
| Full testing | Not run | Separate permission requested for the full automated software suite; no response yet. Application runtime and hardware checks are excluded from that request. |
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
- Logitech support is constrained to documented receiver IDs and real percentage capabilities. Level-only models cannot provide the requested exact battery percentage.
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
- Gated Logitech `1000` percentages on its advertised mileage capability, retaining category-only values as unavailable. Razer admission excludes unknown/wired products while explicitly recognizing the HyperPolling receiver with unavailable battery data.
- Removed unused tray anchoring code and prevented nonpositive HID++ read deadlines. No CPU/memory or real-time performance measurements were taken.
- Added unsigned `win-x64` framework-dependent and self-contained preview packages, provenance manifests, notices, archive/payload checksums and static ZIP/PE/runtime inspection. See [release instructions](release.md). Packages are CI artifacts; no signed/public GitHub Release is claimed.

Source review and authorized compilation, focused regressions and both package inspections passed. Full automated testing remains permission-gated; application runtime and physical hardware verification remain unperformed. Hardware compatibility, reconnect behavior, notifications, visual correctness, startup behavior and actual polling frequency remain open validation work. Preview packages are ready for review; a fully verified release is not claimed.

## Preview downloads

Both packages are version `0.1.0-preview.1`, built with SDK `10.0.402` from source `8bf6aeb` in the Windows run above. Both artifacts were downloaded successfully; local archive checksums and embedded source provenance match the Windows evidence. GitHub artifacts expire after 14 days. Extract the GitHub artifact wrapper, then extract the application ZIP inside it.

| Package | ZIP size | SHA-256 | Download artifact |
| --- | --- | --- | --- |
| Framework-dependent; needs .NET 10 Windows Desktop Runtime x64 | 201,706 bytes | `48cf6cb4906839749d203f2e7530e3a1c47cfa0396ac64046a5d6996bc0ec518` | [Download](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39419305858/artifacts/12263252456) |
| Self-contained | 76,228,336 bytes | `5127e4bf236a66d4690661e04a9e781c02bc39eb038850e0ca4439cb43bb9a92` | [Download](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39419305858/artifacts/12263177484) |

The earlier packaging failure was confined to ZIP timestamp assignment and missing runtime-pack notice text. Windows CI confirmed both fixes in this passing run: timestamps are set before writing entries, and absent pack notices explicitly refer to bundled distribution-wide notices while retaining exact-pack licenses.
