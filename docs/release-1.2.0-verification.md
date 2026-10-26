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
| Unexpected failures | **0**. No source fix or test rerun was required. |

The installer deliberately refused an upgrade while the test held the application mutex (exit 1 with expectedSuccess=false). This is a passing negative test, not a failed installation result. The previous-version installer fixture contains current app bytes and validates lifecycle rather than historical binary migration.

## Existing warnings

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

All prepublication software and artifact checks passed. Publication is authorised and will use the reviewed [manifest](../.github/releases/1.2.0.json) and [notes](../.github/releases/1.2.0.md). Final release/receipt and independent asset verification will be recorded here after the publisher completes.
