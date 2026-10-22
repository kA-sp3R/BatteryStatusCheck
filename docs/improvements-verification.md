# Improvements verification and change record

Candidate version: **1.1.0**. Publication awaits the user's decision after verification. The published v1.0.0 release remains unchanged.

## Verification outcomes

Windows compilation, full automated tests, both package inspections, both isolated WPF smoke runs and installer lifecycle smoke are pending for this candidate. Static XML/YAML/Python syntax, whitespace and peer reviews passed. No physical hardware testing has been performed.

The Windows application smoke launches only an isolated internal test branch using synthetic current0/category/stale/unsupported data. It exercises rendered WPF/settings actions, selected icons, DPI sizes, process-local shell-message recovery, native resource disposal and redacted exports. It bypasses the normal controller, HID, network and startup registration. Installer smoke separately exercises current-user registration, mutex refusal, upgrades, settings/backup/log preservation, obsolete-owned-file cleanup, unowned-file preservation and clean uninstall with owned/foreign startup commands.

## Functions affected

| Area | Change and interactions |
| --- | --- |
| Razer monitoring and manual polling | New evidence-backed E6 mouse receiver uses interface3 with explicit mouse kind. Atheris/Orochi wait400ms instead of60ms before replies. Serialized HID operations can therefore take longer on those receivers. Existing BF/C5/B8 association and charging-failure rules remain. |
| Diagnostics | Discovery/transport decorators log metadata without issuing additional commands; snapshots and manual polling outcomes include original timestamps and validation failures. Storage failures stay separate from battery outcomes. Logs/export are bounded and redacted. |
| Tray and notifications | Single summary remains default. Selected-device mode owns up to16 icons with neutral unknown/category/stale states. Device alerts use a corresponding selected icon or an existing fallback. Existing popup/settings/manual controls remain available from every icon. |
| Preferences | New icon selection and update mode fields use backwards-compatible defaults and independent normalized copies; aliases, notifications, preferred device, refresh interval and startup preferences remain persisted. |
| Updates and shutdown | Notify-only checks stable metadata at startup/daily. Download and install require separate clicks. Off cancels update operations. Staged installers are hash-verified and locked again after graceful shutdown releases the app mutex. |
| Installation | New per-user self-contained installer owns its payload/shortcuts/HKCU uninstall entry; upgrades and uninstall preserve saved data. Exact matching installed startup entries are removed on uninstall; foreign commands remain. Both portable flavors remain available. |
| Release automation | Optional installer and application-smoke evidence can gate a future approved release. Legacy v1.0.0 manifests remain supported. This task does not publish a release. |

## Verification boundaries

Real receivers/firmware and battery accuracy, normal live-monitoring runtime, vendor-software coexistence, notification delivery, actual Explorer restart, physical mixed-monitor DPI, Windows10/11 desktop installation prompts, long-run resource/performance measurements and live release installation remain unverified. Synthetic shell messages do not establish a real Explorer restart. Installer upgrade smoke uses a controlled older-installer fixture with the current app bytes, rather than an actual older executable. The credential-free updater cannot read this private repository's releases anonymously; it reports unavailable and offers the signed-in browser release page. No credentials are embedded or stored.

CLI status/JSON commands, local API, capture-assisted learning and a G Hub data source remain roadmap items. Logitech categories remain native categories; no voltage estimator was introduced.

## Edited files

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
- [docs/installation.md](../docs/installation.md)
- [docs/protocols/razer-polling.md](../docs/protocols/razer-polling.md)
- [docs/protocols/razer.md](../docs/protocols/razer.md)
- [docs/release.md](../docs/release.md)
- [docs/research/razer-wireless.md](../docs/research/razer-wireless.md)
- [docs/research/reference-projects.md](../docs/research/reference-projects.md)
- [docs/updates.md](../docs/updates.md)
- [docs/verification.md](../docs/verification.md)
- [installer/DeviceBatteryStatus.iss](../installer/DeviceBatteryStatus.iss)
- [src/DeviceBatteryStatus.App/App.xaml.cs](../src/DeviceBatteryStatus.App/App.xaml.cs)
- [src/DeviceBatteryStatus.App/ApplicationController.cs](../src/DeviceBatteryStatus.App/ApplicationController.cs)
- [src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj](../src/DeviceBatteryStatus.App/DeviceBatteryStatus.App.csproj)
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
- [docs/improvements-verification.md](improvements-verification.md) (this record)
