# Device Battery Status 0.1.0 preview

Unsigned preview packages for Windows 10/11 **x64**. Windows compilation and automated tests are separate from application runtime and hardware verification. No physical hardware compatibility is certified. See the repository's `docs/verification.md` for the exact checked source and outcomes.

## Choose a package

- **framework-dependent**: smaller download; install the Microsoft **.NET 10 Windows Desktop Runtime x64** first. The base .NET Runtime alone does not include WPF.
- **self-contained**: includes its .NET/Windows Desktop runtime; larger download and extraction. No separate .NET installation is needed. Runtime security updates require a rebuilt package.

Download the chosen `windows-preview-win-x64-...` artifact from the Windows workflow run. GitHub wraps the artifact in a download ZIP containing the app ZIP and its `.sha256` file; extract that wrapper first. Both app ZIPs use a normal multi-file publish layout. Extract the **entire app ZIP** into a writable folder and keep its files together. They do not install a service, driver or scheduled task. Do not run from inside the ZIP. The app accesses USB HID devices while it runs.

Start `DeviceBatteryStatus.exe`. Left-click the tray icon for the device popup; right-click for Settings, Refresh and Exit. Windows startup is off unless you enable it in Settings. Before moving/removing the folder, disable startup in the running copy's Settings and exit. Delete the extracted folder to remove the app. Optional preferences/backups live in `%LOCALAPPDATA%\DeviceBatteryStatus`; delete that directory separately if you also want to remove saved settings.

## Accuracy and limits

- USB 2.4 GHz only. Bluetooth and wired device IDs are excluded.
- Genuine zero is valid. Unavailable, stale and failed values are distinguished; a plugged-in receiver does not prove the peripheral is connected.
- Battery protocols are source-backed implementations, not hardware-certified support. Alienware Pro `413C:A529` has an AWCC-derived reader; Windows/headset validation remains outstanding. Razer generic HyperPolling association is unsupported. Firmware caching and coexistence with vendor applications are unverified.
- Polling-rate control is off each session and changes only through Apply. Rates depend on documented Razer receivers or Logitech's advertised options. Readback confirms configuration, not measured USB timing. Razer banked high-rate protocols remain unsupported.
- Visual placement, notifications, startup registration, reconnect behavior, physical battery accuracy and actual polling frequency require separate Windows runtime/hardware verification.

## Provenance and integrity

Each package includes `THIRD_PARTY_NOTICES.md` and `package-manifest.json` with source commit, GitHub workflow run, SDK version, runtime configuration and SHA-256 hashes for every payload file. Both flavors retain license and third-party notice texts from the active .NET distribution for their apphost. Self-contained packages additionally retain the license and available notice texts from the exact redistributed .NET/Windows Desktop runtime pack versions. If a runtime pack omits its third-party notice text, its manifest entry explicitly references the active distribution's bundled notices instead; those are not claimed to be exact-pack notices. These files and their provenance are recorded under `licenses/` and in the manifest. The ZIP's sibling `.sha256` file hashes the complete archive. From PowerShell, `Get-FileHash .\DeviceBatteryStatus-*.zip -Algorithm SHA256` lets you compare the downloaded ZIP with that checksum. Checksums establish consistency with the supplied manifest; they are not an Authenticode signature or a publisher identity guarantee.

The Windows workflow publishes each flavor, assembles ZIP entries in stable order using the source commit's timestamp, and inspects archive paths, checksums, PE architecture/subsystem and runtime requirements without launching the app. The toolchain and workflow provenance are recorded; packages from different runs intentionally differ in provenance and are not claimed to be byte-identical. CI artifacts are preview deliverables, not a published or signed GitHub Release.
