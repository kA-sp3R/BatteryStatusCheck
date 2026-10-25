# Remaining runtime and gaming alerts

This work extends the unpublished 1.2.0 candidate. The published release remains v1.1.0. Runtime estimates describe observed discharge; capacity-based battery health remains a separate calculation with its existing telemetry requirements.

## Runtime behavior

The estimator observes the monitor's existing publications. It never enumerates HID, sends commands, starts another hardware timer or increases query frequency. A display/freshness tick uses the same five-second presentation timer as before. History is transient and bounded to 64 devices, 24 hours and 2048 stored samples per device; restarting the app starts a new estimation period. Capacity-health history is unchanged and remains persistent.

Only valid, fresh, timestamped percentages with a confirmed connected peripheral and known discharging state enter the trend. Categories never become percentages or estimates. Charging/full/unknown charging, unknown association, stale/invalid/future samples, disconnection, missing devices, conflicting or non-advancing samples, changed receiver/model identity and substantial monitoring gaps invalidate continuity. Windows device changes, sleep/resume and manual polling-rate operations also interrupt the trend. The gap budget follows battery freshness: twice the configured refresh interval plus five seconds. A rise exceeding two points above the period's low or an implausibly abrupt large drop starts a new period.

An estimate requires at least 20 minutes, two separate downward steps and three percentage points lost. Flat and coarse gauges can require considerably longer. Elapsed-time coverage weights handle irregular samples; an exponentially weighted regression smooths short fluctuations. Recent consumption receives more weight, retaining at least a meaningful window and enough coarse drops. Remaining time is the current reported percentage divided by the observed percentage-per-hour discharge rate. Gauge step size and changes between early/late rates widen the uncertainty range; neither capacity nor an assumed power draw enters the calculation. Confident estimates additionally require at least 30 minutes, three drops, three observed step widths and bounded rate/quantization uncertainty. These are operational confidence rules, not a statistical guarantee.

The main popup and relevant tray tooltips show rounded text or ranges, **Calculating…** while evidence accumulates, and **Unavailable** with explanatory popup detail when unsupported. Estimates are anchored to the latest actual sample and never count down through missing observations. Workload changes, firmware-cached percentages and future radio/gaming use can change actual runtime. Protocol-reported identity is not proof of a unique chemical battery: some supported receivers cannot distinguish an undetected swap between identical peripherals.

## Alert settings and delivery

Open **Settings → Gaming alerts**. The existing notification switch is the global master. Older settings retain their percentage threshold; new global defaults use low 20%, critical 10%, eight seconds, a 15-minute cooldown, five-point recovery and sound off. Saving the new editor uses the existing atomic settings store. Main preferences continue to preserve all alert rules.

Global defaults and up to 64 optional device overrides provide percentage/runtime/both triggers, independent native Low/Critical category warnings, low/critical ordering, duration, monitor, sound, snoozing, cooldown and recovery. Low percentage accepts 1–50% and critical accepts 0% below low. Runtime low accepts 2–1440 minutes and critical accepts 1 minute below low. Display duration accepts 3–30 seconds. The editor rejects invalid ordering before saving. Category-only devices explain why percentage/runtime options are unavailable. A global snooze applies to overrides too. The synthetic preview can use unsaved rules without saving preferences or consuming real alert cooldowns.

Percentage and category warnings require fresh current readings; an unknown charging state remains unknown, while confirmed charging/full suppresses alerts. Runtime thresholds additionally require a confident estimate and compare its conservative upper bound, so an optimistic midpoint cannot trigger an early runtime warning. An alert includes the genuine percentage or native category and honest runtime text even if no estimate exists.

One compact black-and-green desktop window shows at a time, with yellow/red severity. It uses `ShowActivated=false`, non-focusable controls, `WS_EX_NOACTIVATE`, `WS_EX_TOOLWINDOW`, `WS_EX_TRANSPARENT`, `WS_EX_LAYERED`, no-activation positioning and explicit mouse-activation/hit-test responses. It never injects into games, captures input, requests foreground focus or minimizes another window. Alerts are click-through and automatically dismissed. No background service is added.

Each triggering source and severity is delivered once until genuine recovery; cooldown expiry alone does not repeat a sustained low warning. Percentage recovery requires the threshold plus its configured margin; runtime recovery requires a confident lower bound above its threshold plus margin; categories recover at native Good/High/Full. Critical escalation can bypass a preceding low warning's cooldown.

The selected monitor's work area determines bottom-right placement; a missing saved monitor falls back to the primary display. Native DPI and window DPI changes control sizing. Windows fullscreen/busy notification state and foreground-window bounds can defer presentation conservatively, including some borderless fullscreen applications. Pending events are bounded to 32 devices and retained for up to ten minutes; ongoing fresh low conditions can qualify again after an expired pending entry. Repeated events coalesce, and unchanged content does not extend a visible alert. Queued and visible alerts are revalidated against current samples, charging, snooze and settings; only actual display acknowledges delivery. Legacy low-battery balloons are no longer invoked; update-available notifications are preserved.

## Files and affected behavior

The implementation and verification harness changed these files (relative to the capacity-only candidate):

- [`.github/scripts/run-application-smoke.ps1`](../.github/scripts/run-application-smoke.ps1)
- [`README.md`](../README.md)
- [`src/DeviceBatteryStatus.App/AlertSettingsWindow.cs`](../src/DeviceBatteryStatus.App/AlertSettingsWindow.cs)
- [`src/DeviceBatteryStatus.App/Alerts/GamingAlertEnvironment.cs`](../src/DeviceBatteryStatus.App/Alerts/GamingAlertEnvironment.cs)
- [`src/DeviceBatteryStatus.App/Alerts/GamingAlertPresenter.cs`](../src/DeviceBatteryStatus.App/Alerts/GamingAlertPresenter.cs)
- [`src/DeviceBatteryStatus.App/Alerts/GamingAlertWindow.cs`](../src/DeviceBatteryStatus.App/Alerts/GamingAlertWindow.cs)
- [`src/DeviceBatteryStatus.App/ApplicationController.cs`](../src/DeviceBatteryStatus.App/ApplicationController.cs)
- [`src/DeviceBatteryStatus.App/PopupWindow.xaml`](../src/DeviceBatteryStatus.App/PopupWindow.xaml)
- [`src/DeviceBatteryStatus.App/PopupWindow.xaml.cs`](../src/DeviceBatteryStatus.App/PopupWindow.xaml.cs)
- [`src/DeviceBatteryStatus.App/SettingsWindow.xaml`](../src/DeviceBatteryStatus.App/SettingsWindow.xaml)
- [`src/DeviceBatteryStatus.App/SettingsWindow.xaml.cs`](../src/DeviceBatteryStatus.App/SettingsWindow.xaml.cs)
- [`src/DeviceBatteryStatus.App/Testing/GamingAlertSmoke.cs`](../src/DeviceBatteryStatus.App/Testing/GamingAlertSmoke.cs)
- [`src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs`](../src/DeviceBatteryStatus.App/Testing/SoftwareSmokeTest.cs)
- [`src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs`](../src/DeviceBatteryStatus.App/Tray/TrayIconManager.cs)
- [`src/DeviceBatteryStatus.App/ViewModels/DeviceRowViewModel.cs`](../src/DeviceBatteryStatus.App/ViewModels/DeviceRowViewModel.cs)
- [`src/DeviceBatteryStatus.Core/Alerts/GamingAlertPolicy.cs`](../src/DeviceBatteryStatus.Core/Alerts/GamingAlertPolicy.cs)
- [`src/DeviceBatteryStatus.Core/Alerts/GamingAlertSettings.cs`](../src/DeviceBatteryStatus.Core/Alerts/GamingAlertSettings.cs)
- [`src/DeviceBatteryStatus.Core/Alerts/GamingBatteryAlert.cs`](../src/DeviceBatteryStatus.Core/Alerts/GamingBatteryAlert.cs)
- [`src/DeviceBatteryStatus.Core/Runtime/RuntimeEstimate.cs`](../src/DeviceBatteryStatus.Core/Runtime/RuntimeEstimate.cs)
- [`src/DeviceBatteryStatus.Core/Runtime/RuntimeEstimator.cs`](../src/DeviceBatteryStatus.Core/Runtime/RuntimeEstimator.cs)
- [`src/DeviceBatteryStatus.Core/Settings/AppSettings.cs`](../src/DeviceBatteryStatus.Core/Settings/AppSettings.cs)
- [`tests/DeviceBatteryStatus.Tests/GamingAlertPolicyTests.cs`](../tests/DeviceBatteryStatus.Tests/GamingAlertPolicyTests.cs)
- [`tests/DeviceBatteryStatus.Tests/GamingAlertSettingsTests.cs`](../tests/DeviceBatteryStatus.Tests/GamingAlertSettingsTests.cs)
- [`tests/DeviceBatteryStatus.Tests/RuntimeEstimateTests.cs`](../tests/DeviceBatteryStatus.Tests/RuntimeEstimateTests.cs)

`RuntimeEstimator` and its result model are independent of capacity health. `ApplicationController` feeds existing samples, interrupts trends at continuity breaks and coordinates the gaming policy/presenter on the dispatcher. Popup rows and summary/selected-device tooltips gain runtime text; tooltip-only changes reuse existing icon bitmaps. The main preferences editor preserves alert configuration and prevents unsaved settings from being overwritten by the new editor. Navigating to alert settings requires closing existing polling/update dialogs first.

Low-battery delivery now uses the custom presenter instead of legacy tray balloons. Default summary mode, selected tray icons, neutral categories/stale states, notifications master, aliases, startup, diagnostics, update balloons and deliberate manual rate controls remain supported. Hardware discovery, protocol providers, transport commands, capacity-health arithmetic/history, installer and updater implementation were not changed by this extension. Existing regression suites and smoke fixtures cover these preserved paths; normal-controller/native integration has the limits below. Alert cooldown/recovery and runtime histories are process-local; saved preferences and existing capacity history survive restarts.

## Verification

Windows compilation, automated tests, isolated application runtime smoke and physical compatibility are separate outcomes. The expanded candidate passed at exact source `1ca669c2b90770d7074cad4b98f156b0031a9240`. [Windows build, packages and runtime smoke](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40233889412), [full software suite](https://github.com/kA-sp3R/BatteryStatusCheck/actions/runs/40233889759), [source/run/count/hash record](../.github/reviews/1.2.0-gaming.json), [candidate notes](../.github/reviews/1.2.0-gaming.md).

| Outcome | Result and scope |
| --- | --- |
| Windows compilation | Passed Release WPF solution build without application launch. |
| Automated C# tests | 1,095 passed in Debug and 1,095 in Release; no failed/skipped cases. Focused Release job also passed 1,095. New coverage comprises 47 runtime, 25 alert-policy and 4 alert-settings cases. Downloaded TRX counters agree with JSON/native exits. |
| Offline parser/release policy | 5 parser and 18 publication-policy cases passed with synthetic files/fake GitHub. |
| Portable package inspection | Both flavors passed; exact source/run, sibling SHA-256, every payload hash and x64 GUI PE headers independently rechecked after download. Framework-dependent ZIP: 391,744 bytes/11 entries; self-contained ZIP: 76,418,618 bytes/482 entries, bundled .NET/Windows Desktop 10.0.14. |
| Isolated application runtime | 18/18 checks per flavor passed, retaining all 12 earlier checks and adding six runtime/alert checks. Actual popup/editor/alert rendered; isolated settings round-trip, validation, category/stale/charging boundaries, threshold crossings, cooldown/recovery, queue/deferral/relevance, preview and disposal passed. Runtime-only tooltip updates did not regenerate icons. |
| Native desktop safety | Actual HWND styles: no-activate, click-through, tool-window and layered; mouse-activation result 3 and transparent hit-test result −1. Foreground snapshot unchanged. Synthetic selected 144-DPI work area contained bounds `(−558,674,540,168)`. Twenty repeated events coalesced into one delivery; preview acknowledged zero real alerts; sound used one fake callback. |
| Installer lifecycle | 64/64 assertions passed: install, mutex refusal, upgrade, reinstallation and uninstall; preferences/logs/capacity history preserved, no app launch. The previous-version fixture contains current app bytes and tests lifecycle rather than historical binary migration. |
| Physical devices and games | Not run; separately unverified. |

New screenshots (`runtime-popup.png`, `gaming-alert.png`, `gaming-alert-settings.png`) and main-settings screenshots were visually reviewed and are identical between flavors. Labels and actions are readable. The fake 144-DPI placement on a runner with different actual font DPI produces extra empty space in the alert screenshot; it does not verify physical DPI transitions. Screenshots and detailed reports are retained in the Actions smoke artifacts; all report/image/log hashes are recorded in the review JSON.

All Windows jobs passed on the first attempt; no failing Windows check required a fix/rerun. Before verification, static review corrected a fullscreen-detection fallback that skipped geometry during normal notification acceptance, prevented closing active polling/update dialogs while navigating to alert settings, and corrected zero/identity/numeric-edge runtime handling. The resulting implementation was then compiled and tested together.

The existing synthetic monitor workload still exercises 8,000 healthy reads in both modes, discovery 8,000→800 and total queries 16,000→12,200 over 200 simulated trials. Its detailed process CPU/memory/handle observations remain in the review JSON; it is a monitor-policy workload, not a direct measurement of runtime-estimator/alert overhead. No new physical or endurance performance claim is made.

The review record supersedes the earlier capacity-only 1.2.0 snapshot. Documentation/evidence-only commits after the tested source do not change the verified binaries. No new tag, release or draft was created. v1.1.0 remains the published release.

Physical battery behavior, real-game/exclusive fullscreen compatibility, actual cross-process click routing, physical monitor hotplug/mixed-DPI behavior, normal live HID monitoring, OS power/Explorer events and extended endurance remain unverified. Synthetic estimates and simulated fullscreen conditions are not evidence of compatibility with real games.

Post-verification documentation/evidence changes: this report, [`docs/verification.md`](verification.md), [expanded candidate JSON](../.github/reviews/1.2.0-gaming.json), [candidate notes](../.github/reviews/1.2.0-gaming.md) and the [earlier review record](../.github/reviews/1.2.0.json), marked as superseded while retaining its original source, hashes and results.
