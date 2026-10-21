# Device Battery Status

Windows 10/11 WPF tray battery monitor for USB 2.4 GHz peripherals, with a circular percentage icon, compact popup, background refresh, notifications, preferences and manual polling-rate controls for supported mice. Milestone 4 adds final source review and unsigned Windows x64 preview packaging. Runtime and physical hardware behavior remain unverified.

## Projects

- `Core`: battery state model, refresh/reconnection coordination, read-only battery protocols, independent manual polling-rate controllers, settings and presentation/notification policies.
- `Windows`: native SetupAPI/HID discovery and cancellable I/O, plus opt-in Windows startup. USB ancestry excludes Bluetooth.
- `App`: one tray icon, a 300-DIP popup, settings, automatic refresh and clean cancellation.
- `Tests`: focused hardware-free state, framing, association, settings and presentation tests. They never enumerate HID or launch the app.

## Tray and preferences

Left-click the tray icon to open the popup; click outside or press Escape to hide it. Right-click for Open, Refresh, Settings and Exit. Starting manually opens the popup; Windows startup runs quietly. A second instance exits without creating another icon or polling loop.

The circular icon shows the lowest fresh percentage, or the selected preferred device. Green is above 30%, yellow is 11–30%, and red includes genuine 0–10%. Devices reporting only a battery category show that category in the popup and tray tooltip, with a neutral icon and no percentage bar. Categories never become estimated percentages or trigger percentage alerts. Stale/unavailable readings use a neutral icon. A preferred device with a category or unavailable reading stays selected. Popup rows retain stale values with clear labels and original timestamps in tooltips; they never count toward the current tray percentage.

Settings are stored at `%LOCALAPPDATA%\DeviceBatteryStatus\settings.json`: refresh interval (10–3600 seconds, default 30), preferred device, custom names, low-battery notifications and optional Windows startup. Names do not change device association. Saves preserve the previous file; invalid files are retained and copied before defaults are used.

Notifications default to 20%, suppress known charging/full states, and require a fresh measured percentage. A new alert requires a fresh recovery at least five percentage points above the threshold. Unknown charging state stays unknown. Windows notification preferences can suppress delivery. Startup is off unless explicitly enabled in Settings and registers only the current `DeviceBatteryStatus.exe` apphost for the current user.

Freshness is checked every five seconds independently of hardware polling. Samples older than twice the refresh interval plus five seconds turn stale, and readings from before sleep stay stale until refreshed. The icon is rendered for the notification area's DPI, including centered `0` and `100`; visual behavior still needs Windows runtime verification.

## Manual polling-rate control

Open **Settings → Polling rate**. Controls start disabled each time the window opens. Enable them, select a mouse or receiver and click **Detect** to read its current configuration and rate options. Select a rate and click **Apply** to request a change. Saving preferences, battery refresh, device selection and startup never change polling rate.

Each Apply checks the target and available rates again, sends its documented setting sequence without retries, and reads back the reported rate. Razer high-rate receivers require two setting commands; cancellation and opt-in are checked before each one. An interrupted or failed sequence may already have changed configuration; detect again before another attempt. For these high-rate receivers, an explicit Apply sends both commands even when the reported rate already matches, allowing a new deliberate attempt after partial completion. Disabling controls, changing devices, closing the window, sleep/resume and shutdown invalidate pending operations and displayed capabilities. Battery refresh and manual exchanges share one gate to prevent competing replies.

| Family | Polling-rate implementation | Limitations |
| --- | --- | --- |
| Razer | Ordinary HyperSpeed receivers at 125/500/1000 Hz; documented high-rate receivers `009F`, `00C1`, `00BF` and generic HyperPolling `00B3` at 125/500/1000/2000/4000/8000 Hz | Options are source-backed profiles, not negotiated limits. Generic `00B3` controls receiver configuration; paired mouse identity/link and battery remain unavailable. Firmware/physical compatibility is unverified. See [protocol coverage](docs/protocols/razer-polling.md). |
| Logitech | Mouse-only HID++ 8060/8061 features, with device-advertised options potentially up to 8000 Hz | Requires fresh pairing identity and compatible receiver/firmware; profile-controlled modes are not changed. See [protocol coverage](docs/protocols/logitech-polling.md). |

These are source-backed implementations; physical compatibility and the actual USB reporting frequency remain unverified. Reading back a configured rate does not measure report timing. Polling-rate failures do not replace battery readings or imply a radio-link state.

An ordinary 1000 Hz HyperSpeed receiver does not gain higher rates through software. A compatible mouse using an upgraded HyperPolling receiver follows that receiver's `00B3` route; its model is not inferred from the dongle.

## Build and verification

Use the **Windows compilation and focused unit tests** GitHub Actions workflow with the .NET 10 SDK. Separate Windows jobs compile the solution, run focused C# tests and test the offline capture parser. They upload logs and TRX results. Following those checks, packaging jobs publish and inspect two portable x64 ZIPs without launching the app:

- **Framework-dependent:** smaller package; requires Microsoft .NET 10 Windows Desktop Runtime x64.
- **Self-contained:** bundles its .NET/Windows Desktop Runtime and notices; requires no separate .NET installation.

Download the relevant `windows-preview-win-x64-…` workflow artifact, extract its package ZIP and keep all files together. Each ZIP has a sibling SHA-256 checksum and an embedded source/run manifest with payload hashes. These are unsigned preview artifacts. See [package instructions and limitations](docs/release.md). The separate `windows-build` artifact is the development compilation output.

Compilation is not application runtime or hardware verification. Obtain explicit permission before launching the app, investigating/testing physical hardware, or running full testing. No hardware compatibility is verified yet. See [verification status](docs/verification.md).

## Battery accuracy and support

`Current` means a validated protocol reply was received in the latest successful refresh, not that the battery has been independently measured. `Stale` retains a previous percentage or reported category and its original sample time after a failure. `Disconnected`, `Unsupported` and `Failed` have neither. A genuine **0% remains 0%**; protocol-defined zero sentinels remain unknown (Logitech `1000` defines level zero as unknown). Battery and charging/link states are independent; unavailable charging/link information stays unknown. Level categories and battery voltages are never converted into invented percentages.

| Family | Implementation | Limitations |
| --- | --- | --- |
| Razer | Mapped wireless mouse/keyboard PIDs; battery and charging control reports | [Exact mappings](docs/protocols/razer.md). Radio-link state unknown; receiver firmware caching unverified. Generic HyperPolling battery/paired-model association unsupported; receiver polling configuration is implemented separately. |
| Logitech | Selected LIGHTSPEED/Unifying receiver PIDs; per-slot HID++ ping, identity, reported percentages or native categories | [Exact coverage](docs/protocols/logitech.md). Category-only readings remain useful without a percentage. Alternative percentage features are checked when supported; device/firmware compatibility remains unverified. |
| Alienware Pro headset | Source-backed protocol reader for `413C:A529` / vendor collection `FF13` | Checks model identity and radio link before accepting battery data. Windows/hardware behavior unverified. Wired `A528` excluded. See [protocol and evidence](docs/protocols/alienware.md). |

Device names in a product family do not establish protocol compatibility. Model-specific PIDs, collection descriptors, firmware and pairing arrangements still need permissioned hardware verification. Discovery failures preserve uncertainty instead of implying disconnection.

For a later Alienware capture, use the [manual capture and offline analysis procedure](docs/research/alienware-capture.md). Its Python helper reads existing files only; it never starts AWCC or sends HID commands. The [AWCC-derived protocol evidence](docs/research/alienware-awcc.md) and [third-party notice](THIRD_PARTY_NOTICES.md) are retained in the repository and notice is included in the application build output.
