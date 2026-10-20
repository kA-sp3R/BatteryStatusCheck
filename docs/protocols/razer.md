# Razer battery protocol

Implemented from protocol facts in [OpenRazer](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d), especially `driver/razercommon.{h,c}`, `razerchromacommon.c`, `razermouse_driver.{h,c}` and `razerkbd_driver.{h,c}`. Source was inspected, not copied. Those OpenRazer files are GPL-2.0-or-later; no OpenRazer binary or source is redistributed here.

The 90-byte Razer message uses command class `07`, battery command `80`, charging command `84`, and two payload bytes. Battery is payload byte 1, scaled from 0–255 to 0–100. A successful zero is **0%**. Windows report ID `00` precedes the message; this implementation supports feature collections with a 91-byte report. XOR covers message offsets 2–87. Replies must match transaction, command, class, framing, payload length and checksum. Busy responses get bounded retries; timeout/failure is unavailable, never a guessed zero or disconnected peripheral.

Known wireless USB product IDs for VID `1532`:

| Devices | PIDs | Transaction |
| --- | --- | --- |
| Viper Ultimate | `007B` | `FF` |
| DeathAdder V2 Pro | `007D` | `3F` |
| Basilisk X HyperSpeed | `0083` | `FF` |
| Basilisk Ultimate; DeathAdder V2 X; Viper Mini SE/V2 Pro; Basilisk V3 Pro; Cobra Pro | `0088`, `009C`, `009F`, `00A6`, `00AB`, `00B0` | `1F` |
| DeathAdder V3 Pro/V3 HyperSpeed/V4 Pro; Viper V3 HyperSpeed/Pro/Pro SE; Basilisk V3 X; Cobra HyperSpeed | `00B7`, `00B8`, `00B9`, `00BF`, `00C1`, `00C3`, `00C5`, `00DB`, `00DF` | `1F` |
| BlackWidow V3 Pro/V3 Mini/V4 Mini/V4 TKL; DeathStalker V2 Pro/Pro TKL | `025C`, `0271`, `0290`, `0296`, `02BA`, `02D5` | `9F` |

Only these wireless IDs are queried. Receiver selection ignores unknown PIDs rather than labeling arbitrary wired Razer USB devices as receivers; the explicitly recognized HyperPolling dongle below remains visible as unsupported. Wired and Bluetooth counterparts are excluded. Replaceable-battery models skip the charging query and retain an unknown charging state. Charging errors do not invalidate an independently successful battery sample. Receiver container identity separates identical models connected through different receivers; the provider never merges devices merely because names or product IDs match.

Control requests must use the correct USB interface. OpenRazer's `razermouse_driver.c:razer_get_report` routes the listed mice through interface `00`; `razerkbd_driver.c:razer_get_report_params` routes wireless keyboard PIDs `025C`, `0290`, `0296`, `02D5` through interface `02`, and `0271`, `02BA` through interface `03`. The provider filters a Windows composite HID path's case-insensitive `&mi_XX` component against this map, rather than choosing a collection merely because its feature length is 91 bytes. Paths without an MI component are accepted for non-composite collections. Actual Windows routing remains unverified on hardware.

Generic HyperPolling `00B3` is detected but **unsupported**: paired model discovery and multi-device association have not been established. Unknown PIDs, shared multi-peripheral receivers and Huntsman variants are unsupported; family names do not establish compatibility. A receiver being enumerated never produces a current battery reading. `Current` requires a matching successful battery command; radio connectivity and whether particular firmware returns cached readings while its peripheral sleeps remain **physically unverified**. No independent radio-link state is claimed. Removed receivers are handled by the monitoring service.

[FerrisPulse](https://github.com/Ferris-echo/FerrisPulse/tree/ffa66068cc5adc149770b58073ab96828ed92092) was also inspected for Windows feature-report framing. Its fallback values, model-name inference and treatment of zero are deliberately not used. [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer/tree/f0e983e88900592237c7df9ae6b80b9dbeb3020b) explicitly supports other concrete devices and does not substantiate Razer compatibility.

Compilation, synthetic protocol tests, application runtime and physical verification are separate. This implementation has not been exercised against physical Razer hardware. Obtain user permission before running the application or interacting with receivers. Do not describe the PID table as hardware-tested compatibility.
