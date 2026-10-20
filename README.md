# Device Battery Status

Windows 10/11 WPF tray battery monitor for USB 2.4 GHz peripherals, with a circular percentage icon, compact popup, background refresh, notifications and preferences. Milestone 3 adds manual polling-rate controls for supported mice. Final testing and release preparation require approval for Milestone 4.

## Projects

- `Core`: battery state model, refresh/reconnection coordination, read-only battery protocols, independent manual polling-rate controllers, settings and presentation/notification policies.
- `Windows`: native SetupAPI/HID discovery and cancellable I/O, plus opt-in Windows startup. USB ancestry excludes Bluetooth.
- `App`: one tray icon, a 300-DIP popup, settings, automatic refresh and clean cancellation.
- `Tests`: focused hardware-free state, framing, association, settings and presentation tests. They never enumerate HID or launch the app.

## Tray and preferences

Left-click the tray icon to open the popup; click outside or press Escape to hide it. Right-click for Open, Refresh, Settings and Exit. Starting manually opens the popup; Windows startup runs quietly. A second instance exits without creating another icon or polling loop.

The circular icon shows the lowest fresh percentage, or the selected preferred device. Green is above 30%, yellow is 11–30%, and red includes genuine 0–10%. Stale/unavailable readings use a neutral icon. A preferred device that becomes unavailable stays neutral rather than silently selecting a different device. Popup rows retain stale measurements with clear labels and original timestamps in tooltips; they never count toward the current tray percentage.

Settings are stored at `%LOCALAPPDATA%\DeviceBatteryStatus\settings.json`: refresh interval (10–3600 seconds, default 30), preferred device, custom names, low-battery notifications and optional Windows startup. Names do not change device association. Saves preserve the previous file; invalid files are retained and copied before defaults are used.

Notifications default to 20%, suppress known charging/full states, and require a fresh measured percentage. A new alert requires a fresh recovery at least five percentage points above the threshold. Unknown charging state stays unknown. Windows notification preferences can suppress delivery. Startup is off unless explicitly enabled in Settings and registers only the current `DeviceBatteryStatus.exe` apphost for the current user.

Freshness is checked every five seconds independently of hardware polling. Samples older than twice the refresh interval plus five seconds turn stale, and readings from before sleep stay stale until refreshed. The icon is rendered for the notification area's DPI, including centered `0` and `100`; visual behavior still needs Windows runtime verification.

## Manual polling-rate control

Open **Settings → Polling rate**. Controls start disabled each time the window opens. Enable them, select a mouse and click **Detect** to read its current rate and supported options. Select a rate and click **Apply** to request a change. Saving preferences, battery refresh, device selection and startup never change polling rate.

Each Apply checks the device and available rates again, sends the setting once, and reads back the reported rate. A failed or interrupted verification cannot establish whether the setting changed; detect again before another attempt. Disabling controls, changing devices, closing the window, sleep/resume and shutdown invalidate pending operations and displayed capabilities. Battery refresh and manual exchanges share one gate to prevent competing replies.

| Family | Polling-rate implementation | Limitations |
| --- | --- | --- |
| Razer | Exact documented wireless receiver IDs using the ordinary 125/500/1000 Hz protocol | High-rate, generic HyperPolling and ambiguous receiver associations remain unsupported. See [protocol coverage](docs/protocols/razer-polling.md). |
| Logitech | Mouse-only HID++ 8060/8061 features, with device-advertised options potentially up to 8000 Hz | Requires fresh pairing identity and compatible receiver/firmware; profile-controlled modes are not changed. See [protocol coverage](docs/protocols/logitech-polling.md). |

These are source-backed implementations; physical compatibility and the actual USB reporting frequency remain unverified. Reading back a configured rate does not measure report timing. Polling-rate failures do not replace battery readings or imply a radio-link state.

## Build and verification

Use the **Windows compilation and focused unit tests** GitHub Actions workflow with the .NET 10 SDK. Separate Windows jobs compile the solution, run focused C# tests and test the offline capture parser. They upload logs and TRX results. The development build artifact requires the matching Windows Desktop Runtime; it is not a release package.

Compilation is not application runtime or hardware verification. Obtain explicit permission before launching the app, investigating/testing physical hardware, or running full testing. No hardware compatibility is verified yet. See [verification status](docs/verification.md).

## Battery accuracy and support

`Current` means a validated protocol reply was received in the latest successful refresh, not that the battery has been independently measured. `Stale` retains a previous percentage and its original sample time after a failure. `Disconnected`, `Unsupported` and `Failed` have no percentage. A genuine **0% remains 0%**. Battery and charging/link states are independent; unavailable charging/link information stays unknown. Level categories and battery voltages are never converted into invented percentages.

| Family | Implementation | Limitations |
| --- | --- | --- |
| Razer | Mapped wireless mouse/keyboard PIDs; battery and charging control reports | [Exact mappings](docs/protocols/razer.md). Radio-link state unknown; receiver firmware caching unverified. Generic HyperPolling association unsupported. |
| Logitech | Selected LIGHTSPEED/Unifying receiver PIDs; per-slot HID++ ping, identity, percentage features/register | [Exact coverage](docs/protocols/logitech.md). Percentage-less models remain unsupported, including many category-only devices. |
| Alienware Pro headset | Source-backed protocol reader for `413C:A529` / vendor collection `FF13` | Checks model identity and radio link before accepting battery data. Windows/hardware behavior unverified. Wired `A528` excluded. See [protocol and evidence](docs/protocols/alienware.md). |

Device names in a product family do not establish protocol compatibility. Model-specific PIDs, collection descriptors, firmware and pairing arrangements still need permissioned hardware verification. Discovery failures preserve uncertainty instead of implying disconnection.

For a later Alienware capture, use the [manual capture and offline analysis procedure](docs/research/alienware-capture.md). Its Python helper reads existing files only; it never starts AWCC or sends HID commands. The [AWCC-derived protocol evidence](docs/research/alienware-awcc.md) and [third-party notice](THIRD_PARTY_NOTICES.md) are retained in the repository and notice is included in the application build output.
