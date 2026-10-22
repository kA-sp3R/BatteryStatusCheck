# Requested reference projects

Research snapshot: 22 October 2026. Sources were inspected as text. No reference application, installer, driver or hardware query was executed, and no reference source, icon asset, binary or voltage lookup table was copied into this project. The implementation independently expresses protocol facts and product behavior. This project’s Windows compilation and software/runtime test results are recorded separately in [verification](../verification.md); external author/user observations are not local hardware verification.

| Reference | Inspected revision | Licence at that revision | Useful evidence and resulting decision |
| --- | --- | --- | --- |
| [xzeldon/razer-battery-report](https://github.com/xzeldon/razer-battery-report/tree/4abf90c280a35fe9c451c17589bedaf237204ec9) | `4abf90c280a35fe9c451c17589bedaf237204ec9` | MIT, copyright 2024 Timofey Gelazoniya | `crates/razer-hid/src/catalog.rs`, `report.rs`, `device.rs`, `discovery.rs` and cited OpenRazer sources supply PID/interface/transaction/wait facts. The README describes per-user installation, diagnostic access and notify-only update defaults. These behaviors informed independently implemented features; automatic installation was not adopted. |
| [Ap0LoG1zE/EZ2.4G](https://github.com/Ap0LoG1zE/EZ2.4G/tree/5db7a8b17400e6449dd1338b985f1763de667c98) | `5db7a8b17400e6449dd1338b985f1763de667c98` | No repository licence found | README and `my_devices.py` illustrate per-device circular numeric tray icons, explicit device selection and capture-assisted investigation. Its documented ATK/MCHOSE mappings do not establish any Razer interface or command. No Razer profile was admitted from this source. The capture-learning workflow remains a roadmap item. |
| [andyvorld/LGSTrayBattery](https://github.com/andyvorld/LGSTrayBattery/tree/5d730a9fd6d8b47d4a0727131bfa4f15b9560fc9) | `5d730a9fd6d8b47d4a0727131bfa4f15b9560fc9` | GPL-3.0 licence text | README documents selected-device icons, numerical display, native HID, optional G Hub data and a local HTTP API. These are product references; no GPL implementation or bundled HID/native binary was transplanted. G Hub/API remain optional roadmap items. Voltage-to-percentage lookup logic was not adopted. |

## Razer comparison and admission

The requested Razer reference has 38 explicitly Dongle-tagged paths. Before this follow-up, this project already covered 37 of them, including generic HyperPolling `00B3`, alongside its keyboard and dock-master profiles. Its wired/BT entries do not expand this project's USB 2.4 GHz scope. Adding the single missing wireless path yields 47 project profiles: 37 mouse paths, six keyboards and four receiver masters.

The additional path is **Viper V4 Pro receiver `1532:00E6`**:

| Fact | Evidence |
| --- | --- |
| Wireless versus wired PID | Reference catalog marks `00E6` Dongle and `00E5` Wired. [OpenRazer PR 2751 head `1f25202a06fc9d37a2475ce71cc0b68fa6bff96c`](https://github.com/openrazer/openrazer/tree/1f25202a06fc9d37a2475ce71cc0b68fa6bff96c), `razermouse_driver.h` and daemon `mouse.py`, explicitly distinguish both forms. Only `00E6` is admitted. |
| Control interface | The same PR's `razer_get_report` selects interface `03` for both forms. [Interface correction](https://github.com/openrazer/openrazer/pull/2751#issuecomment-4180782238) and [external confirmation](https://github.com/openrazer/openrazer/pull/2751#issuecomment-4183899892) explain why the initial interface-0 mapping failed. The PR remains open/unmerged at this snapshot. |
| Battery and charging transaction | PR `razer_attr_read_charge_level` / `razer_attr_read_charge_status` explicitly select transaction `1F` for `00E6`. Reference catalog corroborates `1F`, charging query support and interface 3. |
| Commands/payload | PR `razerchromacommon.c` defines battery `07:80`, charging `07:84`, declared size `02`; value is argument 1. These match the existing independent codec. |
| Framing | PR `razercommon.c` uses feature report value `0300`, interface index 3 and the 90-byte Razer struct. Windows prepends report ID `00` to form the 91-byte feature report. Existing framing, header, declared-size, request-argument and checksum validation remain in force. |
| Wait/type | Reference gives 59,900 microseconds; project keeps conservative 60 ms. Explicit Mouse catalog metadata prevents interface3 from being mistaken for a keyboard and choosing keyboard transaction namespace `81`. |
| Polling writes | No new native `00E6` write profile was enabled by this battery mapping. A model-reported `00E6` child on the separately documented HyperPolling receiver remains a mouse; existing association/opt-in/readback guards still apply to that receiver's rate commands. |

OpenRazer source inspected here is GPL-2.0-or-later. Only independently recorded facts were used, not translated driver code. The unmerged PR and external confirmations are source evidence; local Windows/receiver compatibility still belongs to the hardware verification record.

## Slow receiver response windows

[OpenRazer `477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d), `driver/razermouse_driver.c::razer_get_report`, and `razermouse_driver.h` explicitly route Atheris receiver `0062` and Orochi V2 receiver `0094` through `RAZER_ATHERIS_RECEIVER_WAIT_US = 400000`. The Razer reference independently exposes these as `wait_us:400_000` catalog entries. The previous universal project wait was 60 ms, which could fetch an unfinished response. Both battery and manual polling exchanges now wait 400 ms on those paths, with cancellation before response retrieval. Other profiles retain 60 ms rather than guessing shorter device-specific timing from a name.

## HyperPolling and safeguards retained

`00B3` already has association-aware battery support in this project. Its existing `BF` device list, `C5` primary role and `B8` link codecs were checked against the [pinned manufacturer/capture evidence](razer-wireless.md). A battery reply requires fresh matching product/link evidence; shared devices use explicit Mouse/Keyboard namespaces, unknown roles are refused, and connected USB presence does not establish a live peripheral. The reference's generic dongle label or descriptor serial is not used to invent a paired mouse model or an individual peripheral identity. Same-model replacement and firmware-cached sample age remain explicit protocol limits.

A successful battery reply is retained if the separate charging command fails. Charging remains Unknown and a failed native session is not reused. The reference's combined battery/charging return path can lose the battery if charging fails; that coupling was not adopted. Only exact documented charging bytes 0/1 are accepted, rather than converting arbitrary nonzero bytes to Charging. Genuine battery zero remains valid.

Logitech category readings remain native categories, without estimated percentages or numeric tray ranking/alerts. A future voltage-based percentage would require an explicit Estimate label; this follow-up adds no voltage estimator.

## Optional roadmap

These references do not authorize enabling extra services or capture access. The following stay on the roadmap pending a separate request: read-only status/JSON/diagnostic CLI commands, an optional local battery API, capture-assisted protocol learning and an optional Logitech G Hub data source. No HTTP listener, G Hub process access, capture helper or general-purpose CLI was added by this research.
