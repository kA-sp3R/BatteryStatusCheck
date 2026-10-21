# Deferred verification record

The user deferred unverified and unmeasured work on 21 October 2026 while requesting the remaining implementation work and a v1.0.0 release. These items are recorded for a later session. Deferral does not establish a passing result.

| Area | Deferred work |
| --- | --- |
| Windows application runtime | Launching the app; tray and popup rendering; mixed DPI and work-area placement; notifications; startup registration; single-instance behavior; sleep/resume; clean shutdown. |
| Native Windows USB/HID integration | Physical receiver enumeration, collection routing, access permissions, cancellation/timeouts, unplug/reconnect and coexistence with Synapse, G HUB or AWCC. |
| Hardware compatibility and battery accuracy | Real receiver, peripheral and firmware combinations; comparison with actual battery/charging/link behavior; firmware caching and sample age. |
| Same-model replacement identity | Razer and Alienware model association lacks a documented unique peripheral serial in these paths. Replacement by another physical unit of the same model cannot be proven from PID/model alone; replacement behavior remains unverified. |
| Physical polling behavior | Actual USB report timing, receiver throughput, firmware persistence and the effects of high-rate settings. Protocol readback establishes reported configuration only. |
| Performance and endurance | CPU/memory measurements, responsiveness and extended runtime stability. |
| Automated coverage beyond existing suites | WPF/native integration and end-to-end runtime tests require a separate verification setup; current software tests use managed logic, scripted transports and synthetic files. |

Windows compilation, automated software tests, package inspection and release-asset integrity remain active release checks. No application launch or hardware command is authorized by this deferral. Model support implemented from protocol or driver evidence remains hardware-unverified until the deferred checks are performed.

The v1.0.0 personal-use distribution remains unsigned unless a signing identity is separately supplied. A stable version tag does not establish Authenticode trust or completion of the deferred verification.
