# v1.2.0 verification and failure report

User-authorised full software testing and stable release. Tested source: `d76cd565b6a9ff2e62e51e5092685e2b063984e7`. [Windows build/packages/companion/installer run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40306127179), [full-software run](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40306132491).

## Results

| Check | Result |
| --- | --- |
| Windows compilation | Passed; 0 errors, 18 existing warnings. |
| C# full solution | 1,116 passed each in Debug and Release; zero failures/skips/errors/timeouts. Focused Release also passed 1,116. |
| Parser / automation | 5 / 39 cases passed; synthetic files and fake GitHub only. |
| Companion runtime | 18/18 per portable flavor; exact packaged DLL and self-contained runtime hashes checked. |
| Installer lifecycle | 64/64 assertions; no installed app launch; settings/logs/capacity history retained. |
| Visual review | Runtime popup, gaming alert/settings and main preferences screenshots match the previous verified baseline. |
| Unexpected software-test failures | **0**. No source fix or test rerun was required. |

The installer deliberately refused an upgrade while the test held the application mutex (exit 1 with expectedSuccess=false). This is a passing negative test, not a failed installation result. The previous-version installer fixture contains current app bytes and validates lifecycle rather than historical binary migration.

## Corrected preparation issue

One local preparation check stopped because the CLI returned a GraphQL node ID where the publisher requires a numeric REST release ID. The REST lookup supplied ID 424110328; exact draft target/body and notes hash were rechecked before the manifest was committed or publication dispatched. No assets were published during the failed preparation. Binaries were unchanged, so no software retest was needed. Software checks and the publication workflow passed their first runs.

## Existing warnings

| Code | Instances |
| --- | ---: |
| `CS8601` | 1 |
| `CS9124` | 1 |
| `xUnit2009` | 1 |
| `xUnit2013` | 1 |
| `xUnit2031` | 12 |
| `WFO0003` | 2 |

Counts total 18 and match the preceding lightweight build. CS8601 is guarded by a nonempty version regex and substitutes unknown for null/invalid data; no runtime null failure was demonstrated. CS9124 is duplicate state in a test fake. xUnit codes recommend assertion styles. WFO0003 recommends WinForms DPI configuration; this app uses an intentional WPF PerMonitorV2 manifest, which was retained. Warnings were reviewed and recorded; compilation is not described as warning-free.

## Verification boundaries

The companion runs synthetic data, loads actual packaged application assemblies and verifies the bundled runtime for self-contained distribution. It does not launch the normal production apphost/startup/controller, enumerate HID, contact update servers, register startup or run games. Native window styles/hooks and foreground preservation passed on the CI desktop; actual game/input routing and physical DPI are unverified. Physical battery accuracy, firmware/vendor coexistence, OS power/PnP/Explorer events, normal startup/idle performance and endurance remain unverified.

Current admitted Razer, Logitech and Alienware devices still lack the required capacity telemetry for health; no physical health support was invented. The private repository's credential-free updater remains unavailable anonymously; signed-in browser downloads are available. Packages are unsigned.

## Publication

[v1.2.0](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.2.0) is published stable/latest at exact tested source `d76cd565b6a9ff2e62e51e5092685e2b063984e7` (2026-10-26 21:30:15 AEDT). [Publication workflow](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40307745102) passed, including 22 repeated publisher-policy cases. The [receipt](../.github/releases/1.2.0-publication.json), [independent verification/failure record](../.github/releases/1.2.0-verification.json), [manifest](../.github/releases/1.2.0.json) and [immutable notes](../.github/releases/1.2.0.md) are retained.

All eight published assets were downloaded and checked against API sizes/digests, receipt and sibling checksums. Both portable payloads passed hash/provenance validation. The verification ZIP's actual Debug/Release TRX/JSON, companion reports, installer report, parser/policy logs and notes match the reviewed source and counts. The tag is a commit reference to the exact tested source. No publication-workflow or asset-verification failure occurred.

| Main asset | Bytes | SHA-256 |
| --- | ---: | --- |
| `DeviceBatteryStatus-1.2.0-win-x64-framework-dependent.zip` | 336,191 | `dab6700bfa79c1779b65060926e8663145ddfe3e52399639a69906354ecf16f3` |
| `DeviceBatteryStatus-1.2.0-win-x64-self-contained.zip` | 73,783,628 | `7e4f9142a2942c9d767674808f3a4f042b0df546774a04715e10785cbec3924e` |
| `DeviceBatteryStatus-1.2.0-win-x64-setup.exe` | 54,806,011 | `4cde1f56611d47040a675c7efb89a8dc65091d3369693abc8ac333be68b28cfa` |
| `DeviceBatteryStatus-1.2.0-verification.zip` | 1,213,036 | `e8223cd978f66a11dff75b1f248d6348f32b3d6c48f292fcc4370be74776cb51` |

Documentation/evidence-only commits after the tested source do not rebuild or modify the published assets. The release is unsigned; hardware/normal-runtime boundaries above remain applicable.
