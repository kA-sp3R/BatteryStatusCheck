# Milestones 1–2 verification

| Category | Status | Evidence / next action |
| --- | --- | --- |
| Static review | Completed | Source/peer review, XML/project-reference checks and whitespace checks. This is not compilation or a rendered UI check. |
| Windows compilation | Milestone 1 passed; Milestone 2 pending | Observed successful compile for `be23f36` in run `39362307220`. The current interface changes still need their own Windows result. |
| Focused C# automated tests | Earlier run failed; current changes pending | Run `39362307220` failed its focused test job. Current coverage adds tray selection, expiry, notification hysteresis and settings persistence. Failure details and current results must be resolved before claiming success. |
| Offline capture parser tests | 5 passed | `python3 -m unittest discover -s tools/alienware -p 'test_*.py' -v`; synthetic USBPcap submissions/completions, IRP reuse, zero bytes/percentage fields, filtering and truncation. This does not validate actual capture files or hardware. |
| Full testing | Not run | Reserved for Milestone 4; requires permission. |
| Application runtime | Not run | Requires explicit permission on Windows. |
| Physical hardware | Not run | Requires explicit permission; no compatibility or accuracy certification claimed. |

The app does not start during CI. Hardware tests are not part of automated tests.

Live GitHub API requests now work, despite environment metadata still showing the older policy. Current requests, rather than that metadata, establish API connectivity. Detailed log/artifact redirects use additional hosts that remain blocked here. CI exposes actual test counters and failure messages as check annotations, which can be read through the working API; full logs/TRX files are also retained as artifacts. No Git-note writer or extra repository-write permission is needed.

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
- Milestone 2 was approved by the user. Milestone 3 has not started and requires approval. The interface, shell notification delivery, mixed-DPI placement, startup registration and full shutdown behavior remain unverified at runtime.
