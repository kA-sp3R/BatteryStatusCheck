# Milestones 1–3 verification

Milestone 3 Windows compilation and focused tests are pending. The table below records the previous passing Milestone 2 baseline until the new source is checked.

| Category | Status | Evidence / next action |
| --- | --- | --- |
| Static review | Completed | Source/peer review, XML/project-reference checks and whitespace checks. This is not compilation or a rendered UI check. |
| Windows compilation | Passed | [Windows run `39383950281`](https://github.com/wn622c4xv9-dotcom/BatteryStatusCheck/actions/runs/39383950281), source commit `cd08a6f0fc93dd8db390bfb6d2eca8b6948c1505`; WPF solution compiled and development artifact uploaded. |
| Focused C# automated tests | 182 passed; 0 failed; 0 skipped | Same Windows run/source commit. Actual TRX counters exposed through check `118499230945`: Executed=182, Passed=182, Failed=0, Skipped=0, NativeExit=0. Coverage includes protocols, association, stale/zero states, tray selection, alerts and settings persistence. |
| Offline capture parser tests | 5 passed; 0 skipped | Same Windows run/source commit, check `118499231035`: Ran=5, Skipped=0, NativeExit=0. Synthetic USBPcap submissions/completions, IRP reuse, zero bytes/percentage fields, filtering and truncation. This does not validate actual capture files or hardware. |
| Full testing | Not run | Reserved for Milestone 4; requires permission. |
| Application runtime | Not run | Requires explicit permission on Windows. |
| Physical hardware | Not run | Requires explicit permission; no compatibility or accuracy certification claimed. |

The app does not start during CI. Hardware tests are not part of automated tests.

Live GitHub API requests work. Current requests establish API connectivity; environment metadata alone does not establish whether an operation succeeds. CI exposes actual test counters and failure messages as check annotations through the API; full logs/TRX files are also retained as artifacts. No Git-note writer or extra repository-write permission is needed.

Malformed Razer/Alienware replies now produce failed readings. A valid Razer battery measurement survives an invalid charging reply with charging left unknown. These paths include regressions; the earlier Razer test failure is resolved in the passing source commit above. Documentation-only status commits use `[skip ci]` and do not change that tested application source.

## Milestone 2 implementation

- One DPI-aware circular tray icon; fresh lowest/preferred-device selection, exact percentage colors and neutral unavailable state.
- Compact black/green popup with dynamic height, device glyphs, independent charging/link labels, stale timestamps, keyboard/outside dismissal and monitor work-area placement.
- One serialized background polling loop, independent presentation expiry, refresh after resume and cancellation before exit.
- Refresh interval, aliases, preferred device, low-battery alerts and explicit opt-in Windows startup; settings saves retain a recoverable prior file and do not overwrite unrelated startup commands.
- Focused hardware-free tests cover real zero, stale/preferred selection, alias identity preservation, alert suppression/recovery, settings round trips and corrupt-file preservation.

## Outstanding limitations

- Alienware Pro now has an implementation based on the exact-model MIT project `romain-ncls/awpro`, revision `d92f6df5`, whose documented frames and fixtures come from AWCC captures. This establishes implementation evidence, not local Windows or hardware validation. The original pcaps/descriptors are not public in that repository. See [research](research/alienware-awcc.md).
- Generic Razer HyperPolling and shared receiver association are unsupported. Razer battery replies do not independently prove a live radio connection or exclude firmware-cached data.
- Logitech support is constrained to documented receiver IDs and real percentage capabilities. Level-only models cannot provide the requested exact battery percentage.
- Windows discovery and native I/O, protocol compatibility, reconnect behavior, and firmware caching need application/hardware verification after permission. Read-only query packets still access hardware when the user launches the app.
- Milestone 3 was approved by the user. Milestone 4 requires approval. The interface, shell notification delivery, mixed-DPI placement, startup registration and full shutdown behavior remain unverified at runtime.

## Milestone 3 implementation

- Separate polling-rate window, disabled each time it opens. Detect reads capabilities; only an explicit Apply can change a setting. No automatic restore or persisted rate is used.
- Exact mapped Razer wireless receiver IDs use the documented ordinary 125/500/1000 Hz protocol. High-rate/two-bank and generic HyperPolling associations remain unsupported.
- Logitech HID++ 8060/8061 offers device-advertised mouse rates, potentially through 8000 Hz, after fresh identity checks. Unknown identity, unsupported versions and profile-controlled modes are refused.
- A shared HID gate serializes battery and manual exchanges. Each Apply rechecks capabilities, sends one setting request and verifies reported configuration through readback; uncertain outcomes remain unverified.
- Disable, selection changes, window closure, suspend/resume and exit revoke pending manual operations. Polling failures do not alter battery data.

Configured-rate readback is not a measurement of USB report timing. No polling-rate behavior, firmware compatibility or receiver/peripheral pairing has been verified on hardware.
