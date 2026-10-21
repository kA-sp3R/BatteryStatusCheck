# Verification

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
