# Device Battery Status

Windows 10/11 WPF tray battery monitor for USB 2.4 GHz peripherals, with a circular percentage icon, compact popup, background refresh, notifications, preferences and manual polling-rate controls for supported mice. The current published release is **1.1.0**, unsigned for personal use. It adds diagnostics, selected-device icons and per-user installation/update support. [Improvement results and edited files](docs/improvements-verification.md). [Release page and downloads](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.1.0). Runtime, hardware and performance checks are recorded in the [deferred verification record](docs/deferred-verification.md).

## Projects

- `Core`: battery state model, refresh/reconnection coordination, read-only battery protocols, independent manual polling-rate controllers, settings and presentation/notification policies.
- `Windows`: native SetupAPI/HID discovery and cancellable I/O, plus opt-in Windows startup. USB ancestry excludes Bluetooth.
- `App`: a default summary icon or selected-device icons, a 300-DIP popup, settings, automatic refresh and clean cancellation.
- `Tests`: focused hardware-free state, framing, association, settings and presentation tests. They never enumerate HID or launch the app.

## Tray and preferences

Left-click the tray icon to open the popup; click outside or press Escape to hide it. Right-click for Open, Refresh, Settings and Exit. Starting manually opens the popup; Windows startup runs quietly. A second instance exits without creating another icon or polling loop.

The circular icon shows the lowest fresh percentage, or the selected preferred device. Green is above 30%, yellow is 11–30%, and red includes genuine 0–10%. Devices reporting only a battery category show that category in the popup and tray tooltip, with a neutral icon and no percentage bar. Categories never become estimated percentages or trigger percentage alerts. Stale/unavailable readings use a neutral icon. A preferred device with a category or unavailable reading stays selected. Popup rows retain stale values with clear labels and original timestamps in tooltips; they never count toward the current tray percentage.

Settings are stored at `%LOCALAPPDATA%\DeviceBatteryStatus\settings.json`: refresh interval (10–3600 seconds, default 30), preferred device, custom names, low-battery notifications and optional Windows startup. Names do not change device association. Saves preserve the previous file; invalid files are retained and copied before defaults are used.

Notifications default to 20%, suppress known charging/full states, and require a fresh measured percentage. A new alert requires a fresh recovery at least five percentage points above the threshold. Unknown charging state stays unknown. Windows notification preferences can suppress delivery. Startup is off unless explicitly enabled in Settings and registers only the current `DeviceBatteryStatus.exe` apphost for the current user.

Freshness is checked every five seconds independently of hardware polling. Samples older than twice the refresh interval plus five seconds turn stale, and readings from before sleep stay stale until refreshed. The icon is rendered for the notification area's DPI, including centered `0` and `100`. Isolated Windows smoke exercises native rendering at several sizes; physical mixed-monitor behavior remains unverified.

## Device icons, diagnostics and updates

Settings defaults to the existing single summary icon. Choose **Separate icons for selected devices** to show up to 16 device icons. Genuine percentages retain their circular background and centred text; categories, unknown and stale readings use neutral icons with honest tooltip text and sample times. A missing selected device retains an unavailable icon. With no devices selected, the summary icon remains accessible.

Use **Settings → Open logs** or **Export diagnostic report** for bounded, redacted diagnostics. Reports include version, selected HID collections, protocol metadata/capabilities, validation failures and reading timestamps. Serial numbers, identifying paths and device identities are redacted; raw HID packets, settings and aliases are excluded. See [diagnostics](docs/diagnostics.md).

The release provides a per-user installer, while both portable ZIP flavors remain available. Upgrades preserve saved settings; uninstall removes installed files, shortcuts and owned startup registration while retaining preferences/logs. See [installation](docs/installation.md).

Update checks default to **Notify only**; **Check manually** and **Off** are also available. Checks never download or install automatically. **Check for updates → Download and verify → Install verified update** are separate actions. Downloads must match the GitHub asset digest, sibling checksum and expected size, and are checked again immediately before launching the installer after graceful shutdown. This repository is private, so credential-free in-app checks and downloads are unavailable; open the release page in your signed-in browser. See [updates](docs/updates.md).

## Manual polling-rate control

Open **Settings → Polling rate**. Controls start disabled each time the window opens. Enable them, select a mouse or receiver and click **Detect** to read its current configuration and rate options. Select a rate and click **Apply** to request a change. Saving preferences, battery refresh, device selection and startup never change polling rate.

For a Logitech mouse using onboard profiles, Detect presents a separate **Switch Logitech to software mode on Apply** checkbox. Checking it and clicking Apply can leave the onboard profile, which may change DPI and button behavior. Profile memory is not edited. The controller confirms software mode and reads capabilities again before setting the rate. The extra choice resets with detection/selection; an interrupted Apply may already have changed the mode.

Each Apply checks the target and available rates again, sends its documented setting sequence without retries, and reads back the reported rate. Razer high-rate receivers require two setting commands; cancellation and opt-in are checked before each one. An interrupted or failed sequence may already have changed configuration; detect again before another attempt. For these high-rate receivers, an explicit Apply sends both commands even when the reported rate already matches, allowing a new deliberate attempt after partial completion. Disabling controls, changing devices, closing the window, sleep/resume and shutdown invalidate pending operations and displayed capabilities. Battery refresh and manual exchanges share one gate to prevent competing replies.

| Family | Polling-rate implementation | Limitations |
| --- | --- | --- |
| Razer | Ordinary HyperSpeed and documented high-rate/HyperPolling receiver configuration, with mapped options through 8000 Hz | Source-backed rate profiles and fresh readback. Receiver-reported child association is checked when supplied; unknown identities stay unknown. [Protocol coverage](docs/protocols/razer-polling.md). |
| Logitech | Mouse-only HID++ 8060/8061 baseline functions, including later backwards-compatible feature versions and advertised options potentially through 8000 Hz | Requires fresh pairing/unit identity. Onboard profiles require the separate software-mode checkbox and Apply. [Protocol coverage](docs/protocols/logitech-polling.md). |

These are source-backed implementations. Physical compatibility, firmware persistence and USB reporting-frequency measurements are deferred. Readback confirms the reported configuration. Polling-rate failures do not replace battery readings or imply a radio-link state.

An ordinary 1000 Hz HyperSpeed receiver does not gain higher rates through software. A compatible mouse using an upgraded HyperPolling receiver follows that receiver's `00B3` route; its model is not inferred from the dongle.

## Build and verification

Use the **Windows compilation and focused unit tests** GitHub Actions workflow with the .NET 10 SDK. Separate Windows jobs compile the solution, run focused C# tests and test the offline capture parser. Additional isolated jobs render the WPF UI with synthetic data and smoke-test installer install/upgrade/uninstall; hardware is never queried by those jobs. They upload logs and TRX results. Following those checks, packaging jobs publish and inspect two portable x64 ZIPs without launching the app:

- **Framework-dependent:** smaller package; requires Microsoft .NET 10 Windows Desktop Runtime x64.
- **Self-contained:** bundles its .NET/Windows Desktop Runtime and notices; requires no separate .NET installation.

Download an application ZIP from the [v1.1.0 release page](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.1.0), extract it and keep all files together. Release assets include sibling SHA-256 checksums and the full test evidence bundle. Each application ZIP has an embedded source/run manifest with payload hashes. Alternatively, download a `windows-preview-win-x64-…` Actions artifact and extract its wrapper ZIP first. See [package instructions and limitations](docs/release.md). The separate `windows-build` artifact is the development compilation output.

The README bundled in the v1.1.0 packages is the immutable pre-publication snapshot and still links to v1.0.0. Use the [v1.1.0 release page](https://github.com/kA-sp3R/BatteryStatusCheck/releases/tag/v1.1.0) and this online guide for current downloads and publication status. Published assets have not been rebuilt or modified.

The manual **Full software suite (no application or hardware launch)** workflow tests an exact source commit without filters in Debug and Release on Windows and runs all offline parser tests. The exact checked source, test counts and outcomes are recorded in [verification status](docs/verification.md) and the release evidence bundle. Publication uses a separate manual workflow that checks the reviewed release manifest, successful build/full-test runs, package provenance, payload hashes and uploaded asset digests before publishing the release.

The published v1.1.0 source passed Windows compilation, 921 C# tests in each Debug/Release configuration, 5 parser tests, 18 release-policy tests, eight isolated application-smoke checks per portable flavor and 56 installer lifecycle checks.

Windows compilation, automated tests, isolated application runtime smoke, installer lifecycle smoke and physical hardware are tracked separately. The improvement smoke uses synthetic data and never starts normal monitoring or accesses HID. Normal monitoring, physical hardware and endurance measurements remain deferred; resume those checks only with explicit permission. See [verification status](docs/verification.md) and [deferred work](docs/deferred-verification.md).

## Battery accuracy and support

`Current` means a validated protocol reply was received in the latest successful refresh, not that the battery has been independently measured. `Stale` retains a previous percentage or reported category and its original sample time after a failure. `Disconnected`, `Unsupported` and `Failed` have neither. A genuine **0% remains 0%**; protocol-defined zero sentinels remain unknown (Logitech `1000` defines level zero as unknown). Battery and charging/link states are independent; unavailable charging/link information stays unknown. Level categories and battery voltages are never converted into invented percentages.

| Family | Source-backed software implementation | Evidence and limits |
| --- | --- | --- |
| Razer | Mapped wireless mice/keyboards, HyperPolling child discovery, and mouse/keyboard dual-link battery routing using reported roles | [Exact mappings and protocol evidence](docs/protocols/razer.md). Device-list/link replies are used when available; ambiguous identities/routes remain unavailable. The device supplies no battery sample timestamp. |
| Logitech | 23 explicit Unifying, LIGHTSPEED, Bolt and Nano receiver IDs; per-slot ping, pairing/unit continuity, reported percentages or native categories | [Exact coverage](docs/protocols/logitech.md). Bolt uses its own pairing layout; Nano data needs a usable slot or unit identity. Categories stay categories when no exact percentage is reported. |
| Alienware Pro headset | AWCC-derived protocol reader for `413C:A529` / vendor collection `FF13` | Checks reported model and radio link before accepting battery data. The documented reply does not identify a unique headset serial. [Protocol and evidence](docs/protocols/alienware.md). |

Source-backed implementation does not certify every receiver/firmware combination. Physical compatibility is recorded as deferred work. Discovery failures preserve uncertainty instead of implying disconnection; unknown device fields remain explicit.

For a later Alienware capture, use the [manual capture and offline analysis procedure](docs/research/alienware-capture.md). Its Python helper reads existing files only; it never starts AWCC or sends HID commands. The [AWCC-derived protocol evidence](docs/research/alienware-awcc.md) and [third-party notice](THIRD_PARTY_NOTICES.md) are retained in the repository and notice is included in the application build output.

## Optional roadmap

Read-only status/JSON/diagnostic CLI commands, a local battery API, capture-assisted protocol learning and an optional Logitech G Hub data source remain roadmap items, pending a separate request. No listener or G Hub integration is enabled. [Reference research and licences](docs/research/reference-projects.md) records how the requested projects informed this work.
