# Device Battery Status 0.1.0-preview.3

Unsigned preview packages for Windows 10/11 **x64**. Windows compilation and automated tests are separate from application runtime and hardware verification. No physical hardware compatibility is certified. See the repository's `docs/verification.md` for the exact checked source and outcomes.

[Published prerelease and downloads](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v0.1.0-preview.3). Both application ZIPs use source/tag target `b53b6b777d409f83d081b51a7a3be4f06c1aa45d`. The full existing automated suite passed all 451 C# tests in Debug and again in Release, plus all 5 parser tests. The release includes its test reports, logs and provenance in a verification ZIP with a sibling checksum.

## Choose a package

- **framework-dependent**: smaller download; install the Microsoft **.NET 10 Windows Desktop Runtime x64** first. The base .NET Runtime alone does not include WPF.
- **self-contained**: includes its .NET/Windows Desktop runtime; larger download and extraction. No separate .NET installation is needed. Runtime security updates require a rebuilt package.

Download the chosen application ZIP directly from the release. Release assets have no extra wrapper. For an Actions download instead, choose the `windows-preview-win-x64-...` artifact; GitHub wraps that artifact in a ZIP containing the app ZIP and its `.sha256` file, so extract that wrapper first. Both app ZIPs use a normal multi-file publish layout. Extract the **entire app ZIP** into a writable folder and keep its files together. They do not install a service, driver or scheduled task. Do not run from inside the ZIP. The app accesses USB HID devices while it runs.

Start `DeviceBatteryStatus.exe`. Left-click the tray icon for the device popup; right-click for Settings, Refresh and Exit. Windows startup is off unless you enable it in Settings. Before moving/removing the folder, disable startup in the running copy's Settings and exit. Delete the extracted folder to remove the app. Optional preferences/backups live in `%LOCALAPPDATA%\DeviceBatteryStatus`; delete that directory separately if you also want to remove saved settings.

## Accuracy and limits

- USB 2.4 GHz only. Bluetooth and wired device IDs are excluded.
- Genuine zero is valid. Unavailable, stale and failed values are distinguished; a plugged-in receiver does not prove the peripheral is connected.
- Logitech category-only readings display the reported category without a percentage or percentage bar. Supported alternative percentage features are preferred when available. HID++ `1000` level zero is a protocol-defined unknown sentinel; genuine `0%` from `1004` SOC and legacy `0D` remains valid. Voltage-derived percentages are not used.
- Battery protocols are source-backed implementations, not hardware-certified support. Alienware Pro `413C:A529` has an AWCC-derived reader; Windows/headset validation remains outstanding. Razer generic HyperPolling battery/paired-model association is unsupported. Firmware caching and coexistence with vendor applications are unverified.
- Polling-rate control is off each session and changes only through Apply. Ordinary Razer HyperSpeed receiver profiles stop at 1000 Hz. High-rate `009F`/`00C1`/`00BF` and HyperPolling `00B3` profiles include 125/500/1000/2000/4000/8000 Hz, using two required setting commands with no retries. Explicit same-rate Apply also sends both commands on those receivers, since a partial sequence may already have changed the reported value. Generic `00B3` controls receiver configuration while paired mouse identity/link and battery stay unknown. Logitech options are device-advertised. Readback confirms reported configuration, not measured USB timing or physical compatibility; a partially completed sequence may already have changed the setting.
- Visual placement, notifications, startup registration, reconnect behavior, physical battery accuracy and actual polling frequency require separate Windows runtime/hardware verification.

## Provenance and integrity

Each package includes `THIRD_PARTY_NOTICES.md` and `package-manifest.json` with source commit, GitHub workflow run, SDK version, runtime configuration and SHA-256 hashes for every payload file. Both flavors retain license and third-party notice texts from the active .NET distribution for their apphost. Self-contained packages additionally retain the license and available notice texts from the exact redistributed .NET/Windows Desktop runtime pack versions. If a runtime pack omits its third-party notice text, its manifest entry explicitly references the active distribution's bundled notices instead; those are not claimed to be exact-pack notices. These files and their provenance are recorded under `licenses/` and in the manifest. The ZIP's sibling `.sha256` file hashes the complete archive. From PowerShell, `Get-FileHash .\DeviceBatteryStatus-*.zip -Algorithm SHA256` lets you compare the downloaded ZIP with that checksum. Checksums establish consistency with the supplied manifest; they are not an Authenticode signature or a publisher identity guarantee.

The Windows workflow builds each flavor, assembles ZIP entries in stable order using the source commit's timestamp, and inspects archive paths, checksums, PE architecture/subsystem and runtime requirements without launching the app. The toolchain and workflow provenance are recorded; packages from different runs intentionally differ in provenance and are not claimed to be byte-identical. A separate manual publisher checks the reviewed source/package manifest, successful full tests and every uploaded asset digest before publishing the prerelease. The published packages are unsigned.
