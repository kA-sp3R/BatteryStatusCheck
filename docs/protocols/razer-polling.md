# Razer polling-rate control

Protocol facts were independently implemented from [OpenRazer `477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d). Relevant GPL-2.0-or-later files were inspected, not copied:

- `driver/razerchromacommon.c`: `razer_chroma_misc_get_polling_rate`, `razer_chroma_misc_set_polling_rate`, and the separate `polling_rate2` commands.
- `driver/razermouse_driver.c`: `razer_attr_read_poll_rate` and `razer_attr_write_poll_rate` establish per-PID transaction IDs and banked-write behavior.
- `daemon/openrazer_daemon/hardware/mouse.py` and `hardware/device_base.py`: model definitions and the ordinary default `[125, 500, 1000]` list.

This controller supports only the ordinary **125, 500 and 1000 Hz** protocol over identified 2.4 GHz wireless mouse receivers. There is no 250 Hz encoding in this ordinary protocol; unsupported values are rejected, never rounded to a different setting.

| Wireless receiver PID (VID `1532`) | Model | GET / SET transaction |
| --- | --- | --- |
| `007B` | Viper Ultimate | `FF` / `FF` |
| `007D` | DeathAdder V2 Pro | `3F` / `3F` |
| `0083` | Basilisk X HyperSpeed | `FF` / `FF` |
| `0088` | Basilisk Ultimate | **`FF` / `1F`** |
| `009C`, `00A6`, `00AB`, `00B0` | DeathAdder V2 X, Viper V2 Pro, Basilisk V3 Pro, Cobra Pro | `1F` / `1F` |
| `00B7`, `00B8`, `00B9`, `00C3`, `00C5`, `00DB`, `00DF` | DeathAdder V3 Pro, Viper V3 HyperSpeed, Basilisk V3 X, alternate DeathAdder V3 Pro, DeathAdder V3 HyperSpeed, Cobra HyperSpeed, Viper V3 Pro SE | `1F` / `1F` |

These are receiver-specific mappings. A mouse family having an optional HyperPolling upgrade does not make its ordinary receiver capable of 2000–8000 Hz. Generic HyperPolling `00B3` remains unsupported because its paired peripheral association is not established. Wireless Viper Mini SE `009F`, Viper V3 Pro `00C1`, and DeathAdder V4 Pro `00BF` use the distinct `C0`/`40` high-rate protocol; OpenRazer explicitly writes bank `00` and then bank `01`. This implementation does not guess that one bank is sufficient or send multiple configuration writes. These models are unsupported here, including their lower rates. Wired and Bluetooth devices, keyboards and unknown model/receiver associations are excluded.

Ordinary GET is command class `00`, command `85`, size `01`; SET is command `05`, size `01`. Argument 0 encodes `01` = 1000 Hz, `02` = 500 Hz, `08` = 125 Hz. The 90-byte message has a leading Windows report-ID `00` byte, making 91 bytes. Replies require the expected transaction, class, command, complete payload, successful status and XOR checksum. Polling transaction IDs are deliberately separate from battery-query transactions. The collection must be USB interface `00` when a composite `&mi_XX` path is present.

Before Apply, the controller binds the exact receiver, source model ID, battery-provider device identity, and open transport path, then freshly reads the reported rate. It validates the requested option, checks cancellation and the caller's current opt-in/selection guard immediately before the single SET command, validates its acknowledgement, and queries the rate again. A matching readback confirms **receiver-reported configuration**, not a measured physical USB polling frequency. An unchanged rate needs no configuration write. Get commands travel through a feature transfer but are read queries, not settings changes.

There are no automatic setting-write retries, pairing operations, bank changes, startup restores, persistent pending changes or automatic setting adjustments. A timeout or malformed response ends this operation without reusing a potentially closed transport. Failed/cancelled verification after a write reports that the setting may already have changed; it does not claim success or roll back speculatively. Receiver command timeouts mean Failed, not proof that a peripheral disconnected. Source PID mapping plus a successful reply does not independently establish the radio link.

Compilation, automated tests, application runtime, and physical hardware verification remain separate. Tests use synthetic reports and fake transports. Windows feature routing, physical rate changes, firmware persistence, simultaneous vendor software and actual mouse/receiver compatibility have not been physically verified. Obtain permission before application runtime or hardware testing.
