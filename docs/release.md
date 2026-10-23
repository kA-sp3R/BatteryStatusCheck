# Device Battery Status 1.1.0

Published, unsigned personal-use release for Windows 10/11 **x64**. [v1.1.0 release page and downloads](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.1.0).

This online guide reflects the published release. The README bundled in the v1.1.0 packages is the immutable pre-publication snapshot: it calls the build a candidate and links to v1.0.0. The [v1.1.0 release page](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.1.0) is authoritative for current downloads and publication status. Published assets have not been rebuilt or modified.

Windows compilation, automated software tests, package inspection, isolated application runtime smoke and installer lifecycle smoke are separate release checks. Verification passed at source `367ea4165c528b6767522764ce7eef97a4744d15`: 921 C# tests in each Debug/Release configuration, 5 parser tests, 18 release-policy tests, eight synthetic-data application smoke checks per portable flavor and 56 installer lifecycle assertions. Both tray resource checks returned GDI 40→40 and USER 31→31 after 20 cycles. Exact evidence is recorded in the release verification ZIP and the [verification record](https://github.com/kA-sp3R/BatteryStatusCheck/blob/main/docs/verification.md).

The application smoke bypasses the normal monitor, HID, network and startup registration. Installer smoke never launches the installed app and uses current app bytes with older installer metadata for the upgrade fixture. Normal monitoring runtime, physical hardware compatibility/accuracy, actual Explorer restarts, physical mixed-monitor DPI, notification delivery, live update installation and endurance remain in the [deferred verification record](https://github.com/kA-sp3R/BatteryStatusCheck/blob/main/docs/deferred-verification.md). This release does not certify physical hardware behavior.

## Choose a package

- **per-user installer**: `DeviceBatteryStatus-1.1.0-win-x64-setup.exe` installs the self-contained payload for the current user; no separate .NET installation is needed. See the installer section below.
- **framework-dependent**: smaller download; install the Microsoft **.NET 10 Windows Desktop Runtime x64** first. The base .NET Runtime alone does not include WPF.
- **self-contained**: includes its .NET/Windows Desktop runtime; no separate .NET installation is needed. Runtime security updates require a rebuilt package.

Download the chosen application ZIP directly from the release, together with its `.sha256` checksum if you want to check integrity. Release assets have no extra wrapper. For an Actions download instead, choose the `windows-preview-win-x64-...` artifact; the historical artifact name also carries stable builds. GitHub wraps that artifact in a ZIP containing the app ZIP and checksum, so extract that wrapper first.

Extract the **entire app ZIP** into a writable folder and keep all files together. Both packages use a normal multi-file layout. They do not install a driver, service or scheduled task. Do not run from inside the ZIP. The application accesses USB HID devices while it runs.

Start `DeviceBatteryStatus.exe`. Left-click the tray icon for the device popup; right-click for Settings, Refresh and Exit. Windows startup is off unless enabled in Settings. Before moving/removing the folder, disable startup in the running copy's Settings and exit. Delete the extracted folder to remove the app. Preferences/backups live in `%LOCALAPPDATA%\DeviceBatteryStatus`; remove that directory separately if you also want to remove saved settings.

## Battery support and accuracy

- USB 2.4 GHz receivers only. Bluetooth and wired device IDs are excluded.
- Current, stale, disconnected, unsupported and failed readings remain distinct. Genuine reported zero is valid. A plugged-in receiver does not establish a connected peripheral.
- Razer monitoring uses mapped wireless devices and receiver-reported child identities. HyperPolling and shared mouse/keyboard receivers use documented link/role reports when available. Ambiguous associations remain unavailable; the app does not copy one untargeted battery reply into multiple devices. The protocol supplies no battery sample timestamp, so host receipt time does not prove the battery measurement's age.
- Logitech includes 23 documented Unifying, LIGHTSPEED, Bolt and Nano receiver IDs. Per-slot pairing or device unit identity is required where needed for association. Reported categories are displayed without percentages or percentage bars, and supported direct-percentage alternatives are preferred. HID++ `1000` level zero means unknown; genuine `0%` from `1004` SOC or legacy `0D` remains valid. Voltage-derived percentage estimates are excluded.
- Alienware Pro receiver `413C:A529` has an AWCC-derived reader that checks model and radio-link replies. Its documented identity lacks a unique headset serial. Windows/headset validation is deferred.
- Source-backed protocols describe the implemented software paths. Actual receiver, peripheral and firmware compatibility, vendor-software coexistence and battery accuracy remain in the deferred record.

## Deliberate polling-rate changes

Open **Settings → Polling rate**, enable controls, select a target and click Detect. Select an advertised/profile-supported rate and click Apply. Battery refresh, saving preferences and startup do not change a setting. Controls reset each session.

Ordinary Razer HyperSpeed profiles offer 125/500/1000 Hz. Documented high-rate and HyperPolling profiles offer options through 8000 Hz and require two setting commands. Explicit same-rate Apply sends both phases on those receivers. Unknown receiver options are not invented; reported readback confirms configuration rather than physical USB timing.

Logitech options come from the selected mouse's HID++ capabilities. If Detect reports onboard-profile control, the separate **Switch Logitech to software mode on Apply** checkbox must be selected before Apply can leave that mode. This may change DPI and button behavior. Profile memory is not edited. Software mode, device identity and available rates are checked again before a rate is written. The mode choice is not saved for later automatic use.

A canceled, interrupted or rejected sequence may already have changed the configured rate or profile mode. No automatic retry or rollback is sent. Detect again before another deliberate attempt. Actual report frequency, firmware persistence and physical compatibility are deferred checks.

## Provenance and integrity

Each application ZIP includes `THIRD_PARTY_NOTICES.md` and `package-manifest.json` with the exact source commit, workflow run, SDK/runtime information and a SHA-256 hash for every payload file. Both flavors retain license and notice texts for their .NET apphost; self-contained packages also retain the available texts for the exact runtime packs. A missing pack notice explicitly references the bundled distribution-wide notices, with that provenance retained in the manifest.

Each ZIP has a sibling `.sha256` asset. In PowerShell, `Get-FileHash .\DeviceBatteryStatus-*.zip -Algorithm SHA256` lets you compare a downloaded archive with that checksum. The release verification ZIP contains the software test reports, logs and release/package provenance. Checksums establish consistency; these packages have no Authenticode signature.

The Windows build workflow inspects archive paths, payload hashes, PE architecture/subsystem, runtime metadata and license provenance without launching the app. The separate manual publisher checks the reviewed release policy, successful build and full-test evidence, exact package bytes and uploaded asset digests before publishing. The published stable release is v1.1.0; the saved normal-monitoring, hardware and endurance deferrals remain applicable.

## Per-user installer, diagnostics and device icons

The release includes `DeviceBatteryStatus-1.1.0-win-x64-setup.exe` with a sibling SHA-256 file. It installs the self-contained package for the current user, with no driver/service/admin requirement and no automatic app launch. Close the running app before installing or uninstalling. Saved preferences and diagnostic logs in `%LOCALAPPDATA%\DeviceBatteryStatus` remain through upgrades and uninstall; installed files, owned shortcuts and an exact matching installed-copy startup entry are removed on uninstall.

Settings retains one summary icon by default; separate selected-device icons are optional. Category/unknown/stale icons stay neutral. Open logs/export diagnostics from Settings; local storage is bounded and serials/identifying paths are redacted, with raw reports and saved settings excluded.

Update checks default to notify-only and support manual/off modes. Download and Install are separate explicit actions. Installer size, API SHA-256 digest and checksum must agree; the app rechecks a staged file under a read lease before starting it after shutdown. This establishes asset integrity, not an Authenticode signature. This repository is private, so credential-free in-app checks and downloads are unavailable. Open its release page in your signed-in browser; credentials are not stored by this app.
