# Device Battery Status

Windows 10/11 WPF battery-monitoring foundation for USB 2.4 GHz peripherals. Milestone 1 only; the tray, compact popup, settings, notifications and polling-rate controls are later milestones and require approval.

## Projects

- `Core`: battery state model, refresh/reconnection coordination and read-only vendor protocols.
- `Windows`: native SetupAPI/HID discovery and cancellable HID I/O; USB ancestry excludes Bluetooth.
- `App`: a minimal monitoring window, automatic 30-second refresh and clean cancellation.
- `Tests`: focused hardware-free state, framing, association and failure tests. They never enumerate HID or launch the application.

## Build and verification

Use the **Windows compilation and focused unit tests** GitHub Actions workflow with the .NET 10 SDK. The compile job restores and builds the solution without launching the app. A separate job runs focused Core tests and uploads its TRX results. The build artifact is a development build, not a release package; it requires the matching Windows Desktop Runtime.

Compilation is not application runtime or hardware verification. Obtain explicit permission before launching the app, investigating/testing physical hardware, or running full testing. No hardware compatibility is verified yet. See [verification status](docs/verification.md).

## Battery accuracy and support

`Current` means a validated protocol reply was received in the latest successful refresh, not that the battery has been independently measured. `Stale` retains a previous percentage and its original sample time after a failure. `Disconnected`, `Unsupported` and `Failed` have no percentage. A genuine **0% remains 0%**. Battery and charging/link states are independent; unavailable charging/link information stays unknown. Level categories and battery voltages are never converted into invented percentages.

| Family | Implementation | Limitations |
| --- | --- | --- |
| Razer | Mapped wireless mouse/keyboard PIDs; battery and charging control reports | [Exact mappings](docs/protocols/razer.md). Radio-link state unknown; receiver firmware caching unverified. Generic HyperPolling association unsupported. |
| Logitech | Selected LIGHTSPEED/Unifying receiver PIDs; per-slot HID++ ping, identity, percentage features/register | [Exact coverage](docs/protocols/logitech.md). Percentage-less models remain unsupported, including many category-only devices. |
| Alienware Pro headset | Unsupported | No verified dongle identity/battery protocol found. No speculative queries. See [research](docs/protocols/alienware.md). |

Device names in a product family do not establish protocol compatibility. Model-specific PIDs, collection descriptors, firmware and pairing arrangements still need permissioned hardware verification. Discovery failures preserve uncertainty instead of implying disconnection.
