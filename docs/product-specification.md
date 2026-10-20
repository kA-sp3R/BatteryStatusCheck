# Device Battery Status — Codex Master Prompt

## Objective

Build **Device Battery Status** from scratch: a lightweight Windows 10/11 application that displays the battery levels of 2.4 GHz wireless peripherals directly in the system tray.

**Development environment:**

- Codex Web on macOS.
- Brand-new GitHub repository.
- C# / .NET / WPF.
- GitHub Actions for Windows builds.

Create the project architecture, source code, UI, hardware communication, settings and build workflows from zero.

Prioritise working functionality, simplicity, performance and maintainability.

Make engineering decisions independently.

## 1. Supported devices

Support **2.4 GHz wireless connections only**.

**Razer**

- Wireless mice: Viper, DeathAdder, Basilisk, Cobra and compatible models.
- Wireless keyboards: BlackWidow, Huntsman, DeathStalker and compatible models.
- HyperSpeed and HyperPolling receivers.

**Logitech**

- Gaming mice: G PRO X SUPERLIGHT, G502, G703, G903, G305 and compatible models.
- Compatible productivity mice.
- LIGHTSPEED and other supported 2.4 GHz receivers.

**Alienware**

- Alienware Pro Wireless Gaming Headset through its 2.4 GHz USB dongle.

Automatically detect devices and retrieve genuine battery percentages, charging states and connection information.

Support multiple peripherals, automatic refresh, reconnection and accurate unavailable states.

Use straightforward, reliable receiver identification and device association.

## 2. Hardware communication

Research proven implementations:

- [https://github.com/Ferris-echo/FerrisPulse](https://github.com/Ferris-echo/FerrisPulse)
- [https://github.com/openrazer/openrazer](https://github.com/openrazer/openrazer)
- [https://github.com/pwr-Solaar/Solaar](https://github.com/pwr-Solaar/Solaar)
- [https://github.com/libratbag/libratbag](https://github.com/libratbag/libratbag)
- [https://github.com/yszyszysz233/2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer)

Investigate Razer Synapse, Logitech G HUB and Alienware Command Center when needed.

Use Windows USB/HID APIs and applicable manufacturer protocols.

Implement battery monitoring and device association together when technically useful.

Prefer small, device-specific implementations over complicated universal frameworks.

Ensure battery readings belong to the correct device. Display unavailable information accurately.

## 3. System tray — primary visual feature

**Visual reference:**

[https://github.com/Ap0LoG1zE/EZ2.4G](https://github.com/Ap0LoG1zE/EZ2.4G)

I particularly like its **small circular green system-tray icon displaying the battery percentage inside the circle**.

Inspect its icon-rendering implementation and recreate the same visual concept using C#.

Requirements:

- Small circular battery indicator.
- Percentage centred inside.
- Razer green `#44D62C` above 30%.
- Yellow at 11–30%.
- Red at 0–10%.
- Neutral indicator when battery information is unavailable.
- Readable at Windows notification-area sizes.
- Automatic updates.
- DPI-aware rendering.

Use one main tray icon showing the **lowest currently valid battery percentage** across monitored devices.

Allow the user to choose a preferred device in settings.

The tooltip should identify the device and display relevant battery information.

## 4. Compact battery popup

Clicking the tray icon opens a small black-and-green popup.

Design:

- Approximately 300px wide.
- Approximately 200–220px tall for three devices.
- Background `#080808`.
- Accent `#44D62C`.
- Compact device rows.
- Small device icons.
- Device names and battery percentages.
- Thin battery progress bars.
- Charging and connection indicators.
- Dynamic height based on device count.

Keep the UI minimal, polished and responsive.

Clicking outside or pressing Escape closes the popup.

## 5. Background features

Implement:

- Automatic device discovery.
- Periodic battery updates.
- Connection and reconnection handling.
- Low-battery notifications.
- Configurable refresh interval.
- Custom device names.
- Preferred tray-icon device.
- Optional Windows startup.
- Minimal settings.
- Clean shutdown.

Optimise for low CPU and memory usage.

## 6.  polling-rate control

Support polling-rate detection and adjustment for compatible Razer and Logitech mice.

Available rates depend on the specific device and receiver, potentially from 125 Hz to 8000 Hz.

Provide supported options and an explicit **Apply** button.

Polling-rate adjustment is disabled by default and changes only through deliberate user action.

Keep it independent from battery monitoring.

## 7. Development workflow

Use four milestones.

### Milestone 1 — Application foundation and battery monitoring

Starting from the empty repository:

1. Create the .NET/WPF solution.
2. Establish a simple, maintainable architecture.
3. Configure Windows GitHub Actions compilation.
4. Implement automatic 2.4 GHz receiver discovery.
5. Implement reliable peripheral identification and association.
6. Implement Razer battery monitoring.
7. Implement Logitech battery monitoring.
8. Implement Alienware Pro headset battery monitoring.
9. Handle charging, connection and unavailable states.

Develop related functions together when it improves implementation efficiency.

### Milestone 2 — User interface and tray

Implement:

- Circular battery-percentage tray icon inspired by EZ2.4G.
- Ultra-compact black-and-green popup.
- Background monitoring.
- Multiple-device display.
- Low-battery notifications.
- Minimal settings.

### Milestone 3 — Polling-rate control

Implement safe polling-rate detection and manual adjustment for compatible mice.

### Milestone 4 — Final optimisation, testing and release

1. Review the entire codebase.
2. Fix implementation defects.
3. Simplify unnecessary code.
4. Remove obsolete or duplicated code.
5. Optimise performance.
6. Complete comprehensive automated testing.
7. Fix test failures and regressions.
8. Build and package the Windows application.
9. Assess final release readiness.

**Finish each milestone and wait for my approval before starting the next.**

## 8. Engineering principles

- Focus on implementing the application.
- Keep the architecture simple.
- Write maintainable, efficient C#.
- Research specific problems as needed.
- Reuse useful internal components.
- Improve or refactor code when beneficial.
- Use genuine hardware information.
- Keep device identification accurate.
- Follow applicable third-party licences.
- Build using Windows GitHub Actions.
- Minimise repeated research, redundant tool calls and unnecessary token usage.
- Keep project documentation short and practical.

Compilation is permitted throughout development.

Request permission before application runtime testing, physical hardware investigation or full testing.

Full testing is planned for the final milestone.

## 9. Progress reporting

At the end of each milestone, briefly report:

1. Completed features.
2. Important source changes.
3. Windows build result.
4. Remaining technical problems.
5. Recommended next step.

Keep reports concise and focused on actual progress.

## 10. Start development

The connected GitHub repository is **brand new**.

Build Device Battery Status entirely from scratch.

Create the necessary solution, projects and Windows build workflow.

Begin **Milestone 1** immediately, focusing on automatic 2.4 GHz peripheral discovery and genuine battery monitoring.

Work independently within the milestone.

The final product should be a **small, fast Windows battery monitor with a green circular percentage indicator in the system tray and a compact popup for viewing all supported devices**.

Prioritise practical implementation, reliable hardware support and minimal complexity.


## Additional verification and accuracy requirements

Keep compilation, automated tests, application runtime verification and physical hardware verification separate. Use Windows GitHub Actions for builds. Obtain permission before runtime or hardware testing. Report unsupported or unverified hardware capabilities accurately. Never fabricate battery values. A genuine 0% is valid. Distinguish current, stale, disconnected, unsupported and failed readings. A receiver being plugged in does not necessarily mean its peripheral is connected.
