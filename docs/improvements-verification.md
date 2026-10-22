# Improvements verification and change record

Candidate version: **1.1.0**, tested source `367ea4165c528b6767522764ce7eef97a4744d15`. All final software verification passed. Publication awaits the user's decision; the published v1.0.0 release remains unchanged. The [candidate record](../.github/reviews/1.1.0.json) saves run IDs, counts, package/installer hashes, compiler provenance and evidence-file hashes. [Proposed release notes](../.github/reviews/1.1.0.md) are ready for review.

## Verification outcomes

Compilation, automated tests, isolated application runtime, installer execution and physical hardware are separate outcomes. All .NET compilation and application/installer execution occurred in Windows GitHub Actions. No local .NET compilation or physical hardware query was performed.

| Check | Final result | Evidence |
| --- | --- | --- |
| Windows WPF solution compilation | Passed; no application launch | [Build run 39858821493](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39858821493). |
| Focused C# software suite | 921 passed, zero failed/skipped | Same build run; downloaded TRX counters match the source-bound full reports. |
| Full solution software suite | 921 passed in Debug and 921 in Release; zero failed/skipped | [Full run 39858849245](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39858849245); downloaded TRX and JSON independently agree, exact source and native exit 0 checked. |
| Offline USB capture parser | 5 passed | Full and build runs; synthetic files only. Downloaded full log confirms native exit 0. |
| Release policy and interrupted-upload recovery | 18 passed | Full run; fake GitHub only. Downloaded log confirms native exit 0. |
| Portable package inspection | Both passed | Framework-dependent: 11 files; self-contained: 482 files with bundled .NET/Windows Desktop 10.0.13. Downloaded ZIP checksums, exact source/run provenance, every payload hash and x64 GUI PE headers independently verified. |
| Isolated Windows application runtime smoke | Eight checks passed for each portable flavor | Actual packaged WPF executable, synthetic data only; normal controller, HID, network, startup registration and saved settings bypassed. JSON provenance and counters passed the publication validator. Popup, settings and update screenshots visually reviewed, including complete footer controls/disclosure. |
| Native tray resource cycles | Passed within the eight-check smoke | Both flavors: 20 create/update/DPI/dispose cycles, GDI 40→40 and USER 31→31. Sizes 16/48/64 px and process-local TaskbarCreated delivery exercised. This is not an endurance or real Explorer-restart result. |
| Installer lifecycle smoke | 56 passed, zero failed | Current-user install, mutex refusal, upgrade, reinstall and two uninstall cycles executed. Settings/backup/log bytes, unowned files and foreign startup command preserved; obsolete owned files, owned shortcuts/HKCU registration and exact owned startup removed. Expected mutex-block exit 1; all successful install/uninstall exits 0. Installed payload hashes match the inspected ZIP. |
| Actual publication-evidence validation | Passed; no publication | Both application reports, installer receipt and all 56 uniquely labelled installer assertions accepted by the publication validators. Downloaded installer SHA-256, sibling checksum, size and self-contained payload provenance agree. Inno Setup engine 6.7.1 and compiler/module hashes recorded. |
| Physical hardware and normal live monitoring | Unverified/deferred | No receiver, peripheral, vendor application or normal monitor was accessed. [Saved boundaries](deferred-verification.md). |

The 142 additional C# cases comprise 16 Razer reference/timing/charging cases, 35 diagnostic/redaction cases, 22 tray-selection cases, 4 settings cases and 65 updater cases. Existing battery/polling/model tests remain in the full 921-case suite. The smoke additionally exercised rendered zero/category/stale/unsupported popup rows, settings callbacks and in-memory Save, Off-mode update actions without requests, native icon centring/lifecycle and an actual redacted diagnostic ZIP export.

The installer upgrade fixture uses current app bytes with older installer metadata. It verifies installer lifecycle, not compatibility with an actual older app binary. Installer execution never launched the installed app. The isolated app branch did not execute an installer. No new release or draft was created.

## Failures found and fixed

| Failure | Fix and final verification |
| --- | --- |
| Actual popup rendering threw when `ProgressBar.Value` tried a two-way write to immutable `ProgressValue`. | Set the production binding to `OneWay`. Both final rendered popup checks pass with genuine 0%, category, stale and unsupported states. |
| Settings-load errors could contain an unregistered canonical Logitech serial/unit ID. | Startup diagnostics use a generic error; canonical IDs are also redacted before registration. Two added privacy regressions pass, and the smoke ZIP contains none of its synthetic identifiers. Detailed local UI errors remain available. |
| Installer build read the loader's component file version instead of the engine version. | Use a no-output compiler probe, retain the supported-version guard, and record loader/module hashes. Both actual installers compiled with engine 6.7.1. |
| Installer smoke expected a custom Start menu group ignored by Inno when its group page is disabled. | Verify the actual registered current-user default group and both shortcuts, retaining cleanup assertions. All 56 lifecycle checks pass. |
| Parented-content screenshot capture applied layout offsets twice and cropped footer evidence, including after the initial visual-brush correction. | Capture the live Window root and assert content/footer bounds plus rendered footer pixels. Final screenshots visibly include Save/Cancel and the entire update disclosure. |
| Successful installer stages reused assertion descriptions, so the actual report failed the publication gate's uniqueness rule. | Label initial-install/upgrade/reinstall assertions by stage and reject duplicates during smoke. The final report has 56 unique successful checks and passes the actual publication validator. |

The initial [smoke run 39711867611](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39711867611) and subsequent [run 39856791097](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/39856791097) retain failure evidence. Source `125c762` passed runtime/installer jobs, but independent screenshot and publication-evidence review found the last two harness issues. The final runs above supersede those results for this candidate; they are bound to source `367ea41`.

## Functions affected

| Area | Change and interactions |
| --- | --- |
| Razer monitoring and manual polling | New evidence-backed E6 mouse receiver uses interface 3 with explicit mouse kind. Atheris/Orochi wait 400 ms instead of 60 ms before replies. Serialized HID operations can therefore take longer on those receivers. Existing BF/C5/B8 association and charging-failure rules remain. This battery mapping adds no native E6 polling-write profile; documented HyperPolling receiver controls retain their existing opt-in/association/readback guards. |
| Diagnostics | Discovery/transport decorators log metadata without issuing additional commands; snapshots and manual polling outcomes include original timestamps and validation failures. Storage failures stay separate from battery outcomes. Logs/export are bounded and redacted. |
| Tray and notifications | Single summary remains default. Selected-device mode owns up to 16 icons with neutral unknown/category/stale states. Device alerts use a corresponding selected icon or an existing fallback. Existing popup/settings/manual controls remain available from every icon. |
| Preferences | New icon selection and update mode fields use backwards-compatible defaults and independent normalized copies; aliases, notifications, preferred device, refresh interval and startup preferences remain persisted. |
| Updates and shutdown | Notify-only checks stable metadata at startup/daily. Download and install require separate clicks. Off cancels update operations. Staged installers are hash-verified and locked again after graceful shutdown releases the app mutex. |
| Installation | New per-user self-contained installer owns its payload/shortcuts/HKCU uninstall entry; upgrades and uninstall preserve saved data. Exact matching installed startup entries are removed on uninstall; foreign commands remain. Both portable flavors remain available. |
| Release automation | Optional installer and application-smoke evidence can gate a future approved release. Legacy v1.0.0 manifests remain supported. This task does not publish a release. |

## Verification boundaries

Real receivers/firmware and battery accuracy, normal live-monitoring runtime, vendor-software coexistence, notification delivery, actual Explorer restart, physical mixed-monitor DPI, Windows 10/11 desktop installation prompts, long-run resource/performance measurements and live release installation remain unverified. Synthetic shell messages do not establish a real Explorer restart. Installer upgrade smoke uses a controlled older-installer fixture with the current app bytes, rather than an actual older executable. The credential-free updater cannot read this private repository's releases anonymously; it reports unavailable and offers the signed-in browser release page. No credentials are embedded or stored.

CLI status/JSON commands, local API, capture-assisted learning and a G Hub data source remain roadmap items. Logitech categories remain native categories; no voltage estimator was introduced.

## Edited files

50 files changed relative to the completed v1.0.0 record. Documentation/evidence-only changes after the tested source do not change candidate binaries.

- [.github/reviews/1.1.0.json](../.github/reviews/1.1.0.json)
- [.github/reviews/1.1.0.md](../.github/reviews/1.1.0.md)
- [.github/scripts/build-installer.ps1](../.github/scripts/build-installer.ps1)
- [.github/scripts/publish-verified-preview.py](../.github/scripts/publish-verified-preview.py)
- [.github/scripts/run-application-smoke.ps1](../.github/scripts/run-application-smoke.ps1)
- [.github/scripts/test-installer.ps1](../.github/scripts/test-installer.ps1)
- [.github/scripts/test_publish_verified_preview.py](../.github/scripts/test_publish_verified_preview.py)
- [.github/workflows/windows-build.yml](../.github/workflows/windows-build.yml)
- [.gitignore](../.gitignore)
- [README.md](../README.md)
- [docs/deferred-verification.md](../docs/deferred-verification.md)
- [docs/diagnostics.md](../docs/diagnostics.md)
- [docs/improvements-verification.md](../docs/improvements-verification.md)
- [docs/installation.md](../docs/installation.md)
- [docs/protocols/razer-polling.md](../docs/protocols/razer-polling.md)
- [docs/protocols/razer.md](../docs/protocols/razer.md)
- [docs/release.md](../docs/release.md)
- [docs/research/razer-wireless.md](../docs/research/razer-wireless.md)
- [docs/research/reference-projects.md](../docs/research/reference-projects.md)
- [docs/updates.md](../docs/updates.md)
- [docs/verification.md](../docs/verification.md)
- [installer/DeviceBatteryStatus.iss](../installer/DeviceBatteryStatus.iss)
- [installer/compiler-probe.iss](../installer/compiler-probe.iss)
- [src/DeviceBatteryStatus.App/App.xaml.cs](../src/DeviceBatteryStatus.App/App.xaml.cs)
- [src/DeviceBatteryStatus.App/ApplicationController.cs](../src/DeviceBatteryStatus.App/ApplicationController.cs)
- [src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj](../src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj)
- [src/DeviceBatteryStatus.App/PopupWindow.xaml](../src/DeviceBatteryStatus.App/PopupWindow.xaml)
- [src/DeviceBatteryStatus.App/SettingsWindow.xaml](../src/DeviceBatteryStatus.App/SettingsWindow.xaml)
- [src/DeviceBatteryStatus.App/SettingsWindow.xaml.cs](../src/DeviceBatteryStatus.App/SettingsWindow.xaml.cs)
- [src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs](../src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs)
- [src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs](../src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs)
- [src/DeviceBatteryStatus.App/Tray/TrayIconRenderer.cs](../src/DeviceBatteryStatus.App/Tray/TrayIconRenderer.cs)
- [src/DeviceBatteryStatus.App/Tray/TrayIconService.cs](../src/DeviceBatteryStatus.App/Tray/TrayIconService.cs)
- [src/DeviceBatteryStatus.App/UpdateWindow.cs](../src/DeviceBatteryStatus.App/UpdateWindow.cs)
- [src/DeviceBatteryStatus.Core/Diagnostics/DiagnosticHid.cs](../src/DeviceBatteryStatus.Core/Diagnostics/DiagnosticHid.cs)
- [src/DeviceBatteryStatus.Core/Diagnostics/DiagnosticLog.cs](../src/DeviceBatteryStatus.Core/Diagnostics/DiagnosticLog.cs)
- [src/DeviceBatteryStatus.Core/Polling/RazerPollingRateController.cs](../src/DeviceBatteryStatus.Core/Polling/RazerPollingRateController.cs)
- [src/DeviceBatteryStatus.Core/Presentation/TrayIconSelection.cs](../src/DeviceBatteryStatus.Core/Presentation/TrayIconSelection.cs)
- [src/DeviceBatteryStatus.Core/Protocols/RazerBatteryProvider.cs](../src/DeviceBatteryStatus.Core/Protocols/RazerBatteryProvider.cs)
- [src/DeviceBatteryStatus.Core/Protocols/RazerDeviceCatalog.cs](../src/DeviceBatteryStatus.Core/Protocols/RazerDeviceCatalog.cs)
- [src/DeviceBatteryStatus.Core/Settings/AppSettings.cs](../src/DeviceBatteryStatus.Core/Settings/AppSettings.cs)
- [src/DeviceBatteryStatus.Core/Updates/GitHubUpdateService.cs](../src/DeviceBatteryStatus.Core/Updates/GitHubUpdateService.cs)
- [src/DeviceBatteryStatus.Core/Updates/ReleaseVersion.cs](../src/DeviceBatteryStatus.Core/Updates/ReleaseVersion.cs)
- [src/DeviceBatteryStatus.Core/Updates/UpdateModels.cs](../src/DeviceBatteryStatus.Core/Updates/UpdateModels.cs)
- [tests/DeviceBatteryStatus.Tests/DiagnosticHidTests.cs](../tests/DeviceBatteryStatus.Tests/DiagnosticHidTests.cs)
- [tests/DeviceBatteryStatus.Tests/DiagnosticLogTests.cs](../tests/DeviceBatteryStatus.Tests/DiagnosticLogTests.cs)
- [tests/DeviceBatteryStatus.Tests/RazerReferenceExpansionTests.cs](../tests/DeviceBatteryStatus.Tests/RazerReferenceExpansionTests.cs)
- [tests/DeviceBatteryStatus.Tests/SettingsExtensionsTests.cs](../tests/DeviceBatteryStatus.Tests/SettingsExtensionsTests.cs)
- [tests/DeviceBatteryStatus.Tests/TrayIconSelectionTests.cs](../tests/DeviceBatteryStatus.Tests/TrayIconSelectionTests.cs)
- [tests/DeviceBatteryStatus.Tests/UpdateServiceTests.cs](../tests/DeviceBatteryStatus.Tests/UpdateServiceTests.cs)
