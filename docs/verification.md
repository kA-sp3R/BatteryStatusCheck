# Verification

## v1.2.0 release

[v1.2.0](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.2.0) is published stable/latest and unsigned, at exact source `d76cd565b6a9ff2e62e51e5092685e2b063984e7`. Fresh Windows verification passed: 1,116 C# tests each Debug/Release, 5 parser/39 automation cases, 18 companion checks per flavor and 64 installer assertions. Publication policy reran 22 cases. **Zero failed/skipped tests; 0 compiler errors and 18 reviewed existing warnings.** One local release-ID mismatch was corrected before publication; binaries were unchanged. [Complete verification/failure report](release-1.2.0-verification.md).

[Build/package/companion/installer run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40306127179), [full suite](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40306132491), [publication run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40307745102). [Manifest](../.github/releases/1.2.0.json), [receipt](../.github/releases/1.2.0-publication.json), [independent verification](../.github/releases/1.2.0-verification.json). All eight downloaded assets match API/receipt/checksum and source. Verification ZIP SHA-256: `e8223cd978f66a11dff75b1f248d6348f32b3d6c48f292fcc4370be74776cb51`.

Companion software runtime, installer lifecycle and physical hardware are separate outcomes. Production startup/controller, actual devices, games/input routing, physical DPI/OS events and endurance remain unverified. No physical capacity-health support was invented. Later documentation/evidence commits do not modify released binaries.

## Earlier lightweight 1.2.0 snapshot

The optimized unpublished candidate passed at source `46123738ffa203b4978bc06d8fa94e97d34c75ab`: Windows compilation, 1,116 C# cases each in Debug/Release, 5 parser/39 automation cases, both portable inspections, 18 companion checks per flavor and 64 installer lifecycle assertions. [Build/packages/companion run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40236243538), [full software run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40236244173), [source/run/count/hash record](../.github/reviews/1.2.0-lightweight.json).

Framework-dependent download fell 391,744→336,205 bytes (14.18%); self-contained 76,418,618→73,783,640 (3.45%). Extracted payload fell by 140,239 bytes each. All 468 bundled runtime/localisation files and notices retain their hashes. [Full report](lightweight-optimization.md) records exact package/extracted sizes, contributors, file/dependency changes, actual synthetic CPU/memory/handle observations, tradeoffs and remaining limits.

Verification fixtures now run in a separate companion that loads the exact packaged DLLs and, for self-contained, the actual bundled runtime. The production apphost/startup/controller is not launched. Screenshots match the baseline exactly; no physical hardware, network, startup registration or games are accessed. Startup/idle performance, real input routing, physical DPI/power/PnP/Explorer events and endurance remain unverified. Size reduction does not establish lower CPU/memory. At that earlier snapshot no new release existed; v1.1.0 was published.

## Earlier runtime/gaming 1.2.0 snapshot

The expanded unpublished 1.2.0 candidate passed at source `1ca669c2b90770d7074cad4b98f156b0031a9240`: Windows compilation; 1,095 C# cases each in Debug/Release (zero failed/skipped); 5 parser/18 release-policy cases; both portable inspections; 18 isolated application checks per flavor; and 64 installer lifecycle assertions. [Build/package/smoke run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40233889412), [full-software run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40233889759), [source/run/hash review](../.github/reviews/1.2.0-gaming.json).

The [full change report](runtime-and-gaming-alerts.md) lists all files, behavior, affected existing functions, exact test scope and remaining limits. Native alert styles/hooks and unchanged foreground snapshot passed on the CI desktop; twenty simulated events coalesced to one. Actual cross-process click routing and real-game/exclusive fullscreen compatibility remain unverified. Software smoke bypasses the normal controller/HID/network/startup and uses synthetic battery/monitor/fullscreen/sound inputs. Capacity health remains unavailable on all admitted physical devices for insufficient telemetry. At that earlier snapshot no new release or draft existed; v1.1.0 was published.

## Earlier capacity-only 1.2.0 snapshot

The earlier capacity-only snapshot passed Windows verification at source `fb41c9e63ce24cf68dbf63d6add4503597bc474c`: compilation, 1019 C# tests each in Debug/Release, 5 parser/18 release-policy cases, both portable inspections, 12 isolated application-smoke checks per flavor and 64 installer lifecycle assertions. [Build/smoke run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40176230540), [full-software run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40176230772), [source/run/hash record](../.github/reviews/1.2.0.json).

The [complete change report](capacity-health-and-efficiency.md) records all edited files, affected functions, failed/fixed verification, actual CPU/memory/handle observations and remaining limits. Synthetic workload results: discovery 8000→800, queries 16000→12200 with healthy reads 8000→8000. CPU observations were 187.5→125 ms and 93.75→62.5 ms across the two flavors; memory/GC snapshots are reported without steady-state or physical claims.

No admitted real model exposes sufficient capacity telemetry for health. Calculation/continuity/history, actual details rendering/design editing, unchanged presentation, simulated notifications and installer preservation passed software checks. Normal live monitoring, actual OS power/PnP/Explorer events, physical battery behavior and endurance remain unverified. The normal controller/HID/network/startup/preferences were bypassed by application smoke. At that earlier snapshot no 1.2.0 release or draft had been created.

## V1.1.0 release checks

The **published stable v1.1.0 release** passed final Windows verification at source `367ea4165c528b6767522764ce7eef97a4744d15`: compilation, 921 C# tests in each Debug/Release configuration, 5 parser tests, 18 release-policy tests, both portable inspections, eight isolated application-smoke checks per flavor and 56 installer lifecycle assertions. Downloaded TRX/JSON/logs, payload hashes and the actual publication-evidence validators independently agree. [Build/smoke run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39858821493), [full-software run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39858849245), [source-bound review record](../.github/reviews/1.1.0.json).

Compilation, automated tests, isolated application runtime, installer lifecycle and physical hardware remain separate outcomes. The runtime smoke launches the packaged WPF app through an internal synthetic-data branch, bypassing the normal controller, HID, network and startup registration. It verifies rendered UI, settings callbacks/Save, disabled update actions, native icons/DPI sizes/simulated shell recovery, 20 disposal cycles and a redacted export. Both flavors returned GDI 40→40/USER 31→31; screenshots were visually reviewed. Installer smoke separately executes install/upgrade/uninstall, verifies registration, mutex refusal, payload hashes, preserved data and cleanup, without starting the installed app. Its older-version fixture contains current app bytes. Neither establishes normal monitoring, physical battery accuracy or interactive desktop behavior.

The [complete improvement report](improvements-verification.md) lists edited files, affected functions, failures found/fixed and remaining boundaries. Live updates cannot be retrieved anonymously from this private repository; the signed-in browser fallback is available. After user approval, [v1.1.0](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.1.0) was published as stable/latest by [publication run 39875565589](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39875565589). All eight downloaded assets independently matched their API sizes/digests, the exact tested source/tag, embedded payload hashes, actual TRX/smoke counts and reviewed screenshots. [Permanent publication receipt](../.github/releases/1.1.0-publication.json). Verification ZIP SHA-256: `017bf307512f30feca398d9509120a680a3b8a1ec2bff38d686cb811fae51de3`. Packages remain unsigned. Documentation/evidence-only commits after the tested source do not change released binaries.

The v1.0.0 release separates compilation, automated software tests, static package inspection, application runtime and physical hardware verification. Runtime, hardware and performance work is saved in the [deferred verification record](deferred-verification.md) and is not an active release gate at the user's request. Deferral is not a passing result.

## V1.0.0 release checks

Application source: `c2c5d747a0952da7e34826d1cfdd117f7f93bd52`. No application launch or physical HID exchange is part of these workflows. The automated suites exercise managed logic, scripted transports, synthetic capture files and a fake GitHub API.

| Category | Result | Evidence |
| --- | --- | --- |
| Windows compilation | Passed | [Build run 39679709333](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39679709333), WPF solution Release compilation. |
| Focused C# software tests | 779 passed, 0 failed, 0 skipped | Same build run; check 119498682702 reports actual TRX counters and native exit 0. |
| Full solution software tests | 779 passed in Debug and 779 in Release; 0 failed/skipped | [Full run 39679716090](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39679716090), source and test automation both at the exact commit above. Downloaded TRX and JSON reports independently agree on all counters. |
| Offline capture parser | 5 passed, 0 failed/skipped | Full run and build run; downloaded log confirms native exit 0. Synthetic files only. |
| Release policy and interrupted-upload recovery | 11 passed, 0 failed/skipped | Full run; downloaded log confirms native exit 0. GitHub operations are fake in these tests. |
| Portable packages | Both passed static inspection | Same build run, checks 119499659089 and 119499659134. Framework-dependent: 11 files; self-contained: 482 files, bundled .NET/Windows Desktop 10.0.13. Downloaded archives independently passed source/run provenance, checksum, every payload hash and x64 GUI PE inspection. No launch. |
| Stable release publication | Published; latest; all six asset digests verified | [v1.0.0](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.0.0), [publication run 39680261533](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39680261533). Downloaded receipt and verification ZIP independently matched tag/source, actual test counters and all six API asset sizes/digests. [Permanent receipt](../.github/releases/1.0.0-publication.json). |
| Application runtime, native/hardware integration and performance | Deferred | [Saved record](deferred-verification.md); no passing result claimed. |

Verification ZIP SHA-256: `05807e2a27fb4d72ade251ee6e7cc1228ed77cde2398e166766b2ed74083c000`. The release is stable, latest and unsigned. Documentation/evidence-only commits after the tested source do not change the released binaries.

The publication workflow requires an exact source commit, successful build and full-test runs, actual test counters, package and payload hashes, unchanged release notes and matching tag provenance. Both package flavors and their immutable verification bundle must match the reviewed manifest before the stable release becomes public. Packages are unsigned.

## Implemented software work

- Razer receiver association uses independently implemented native device-list, primary-device and connection queries. HyperPolling battery reads bind to a reported model before and after the measurement. Shared mouse/keyboard receivers use distinct transaction namespaces and reject ambiguous associations; omission establishes disconnection only after a complete association scan.
- The Razer catalog adds documented wireless models and receiver paths. Manual polling reads and changes retain explicit opt-in, association checks and readback. Source-profile rate options describe implemented commands; they do not establish negotiated firmware capabilities or measured USB timing.
- Logitech adds documented Nano and Bolt receivers, fresh identity continuity and definitive versus incomplete slot sweeps. Native battery categories remain categories. An alternative feature supplies an exact percentage only when its advertised capabilities and value encoding authorize it.
- Logitech polling accepts documented backwards-compatible feature revisions. A separate checkbox plus Apply can explicitly leave an on-board profile for software mode, then re-probe capabilities and apply a selected rate. This may change active DPI/button behavior; profile memory is not written.
- Genuine zero values, stale sample times, explicit disconnected states and failed/unsupported readings remain separate. A receiver's USB presence alone never establishes a connected peripheral.

## Earlier preview evidence

[v0.1.0-preview.3](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v0.1.0-preview.3) used source `b53b6b777d409f83d081b51a7a3be4f06c1aa45d`. Its [Windows build](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39527777290), [full tests](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39528608946) and [publication](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39529174142) passed: 451 C# cases per configuration and 5 parser cases, with both unsigned packages inspected. These results apply to that earlier source, not subsequent changes. The manifest remains in [the preview record](../.github/releases/0.1.0-preview.3.json).
