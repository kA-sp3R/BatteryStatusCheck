# Device Battery Status

Windows 10/11 WPF tray battery monitor for USB 2.4 GHz peripherals. Milestone 2 adds the circular percentage icon, compact popup, background refresh, notifications and preferences. Polling-rate control and release preparation require approval for Milestones 3 and 4.

## Projects

- `Core`: battery state model, refresh/reconnection coordination, read-only protocols, settings and presentation/notification policies.
- `Windows`: native SetupAPI/HID discovery and cancellable I/O, plus opt-in Windows startup. USB ancestry excludes Bluetooth.
- `App`: one tray icon, a 300-DIP popup, settings, automatic refresh and clean cancellation.
- `Tests`: focused hardware-free state, framing, association, settings and presentation tests. They never enumerate HID or launch the app.

## Tray and preferences

Left-click the tray icon to open the popup; click outside or press Escape to hide it. Right-click for Open, Refresh, Settings and Exit. Starting manually opens the popup; Windows startup runs quietly. A second instance exits without creating another icon or polling loop.

The circular icon shows the lowest fresh percentage, or the selected preferred device. Green is above 30%, yellow is 11–30%, and red includes genuine 0–10%. Stale/unavailable readings use a neutral icon. A preferred device that becomes unavailable stays neutral rather than silently selecting a different device. Popup rows retain stale measurements with clear labels and original timestamps in tooltips; they never count toward the current tray percentage.

Settings are stored at `%LOCALAPPDATA%\DeviceBatteryStatus\settings.json`: refresh interval (10–3600 seconds, default 30), preferred device, custom names, low-battery notifications and optional Windows startup. Names do not change device association. Saves preserve the previous file; invalid files are retained and copied before defaults are used.

Notifications default to 20%, suppress known charging/full states, and require a fresh measured percentage. A new alert requires a fresh recovery at least five percentage points above the threshold. Unknown charging state stays unknown. Windows notification preferences can suppress delivery. Startup is off unless explicitly enabled in Settings and registers only the current `DeviceBatteryStatus.exe` apphost for the current user.

Freshness is checked every five seconds independently of hardware polling. Samples older than twice the refresh interval plus five seconds turn stale, and readings from before sleep stay stale until refreshed. The icon is rendered for the notification area's DPI, including centered `0` and `100`; visual behavior still needs Windows runtime verification.

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
