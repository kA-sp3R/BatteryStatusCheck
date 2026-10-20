# Razer polling-rate control

Protocol facts were independently implemented from [OpenRazer `477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d). The relevant GPL-2.0-or-later files were inspected, not copied:

- `driver/razerchromacommon.c`: ordinary polling commands and the separate `polling_rate2` encoding.
- `driver/razermouse_driver.c`: `razer_attr_read_poll_rate` and `razer_attr_write_poll_rate`, including per-PID transaction IDs and the required two-command sequence.
- `daemon/openrazer_daemon/hardware/mouse.py` and `hardware/device_base.py`: USB model mappings and polling-rate profiles. The generic HyperPolling class explicitly leaves paired-mouse identification as a TODO.

Only documented USB 2.4 GHz receiver paths are selected. Wired and Bluetooth devices, keyboards and unknown product IDs are excluded. Source coverage is implementation evidence; physical mouse, receiver and firmware compatibility remains unverified.

## Ordinary HyperSpeed receivers

The ordinary profile offers **125, 500 and 1000 Hz**. These receiver-specific mappings do not acquire higher rates merely because their mouse supports an optional HyperPolling upgrade.

| Receiver PID (VID `1532`) | Model | GET / SET transaction |
| --- | --- | --- |
| `007B` | Viper Ultimate | `FF` / `FF` |
| `007D` | DeathAdder V2 Pro | `3F` / `3F` |
| `0083` | Basilisk X HyperSpeed | `FF` / `FF` |
| `0088` | Basilisk Ultimate | **`FF` / `1F`** |
| `009C`, `00A6`, `00AB`, `00B0` | DeathAdder V2 X, Viper V2 Pro, Basilisk V3 Pro, Cobra Pro | `1F` / `1F` |
| `00B7`, `00B8`, `00B9`, `00C3`, `00C5`, `00DB`, `00DF` | DeathAdder V3 Pro, Viper V3 HyperSpeed, Basilisk V3 X, alternate DeathAdder V3 Pro, DeathAdder V3 HyperSpeed, Cobra HyperSpeed, Viper V3 Pro SE | `1F` / `1F` |

Ordinary GET uses class `00`, command `85`, declared size `01`; SET uses command `05`, size `01`. Argument 0 encodes `08` = 125 Hz, `02` = 500 Hz, `01` = 1000 Hz. There is no ordinary 250 Hz encoding. Unsupported values are rejected without rounding or substituting another setting.

## High-rate receivers

The high-rate profile offers **125, 500, 1000, 2000, 4000 and 8000 Hz**, following the exact OpenRazer profiles. These are static documented options, not device-advertised or negotiated capabilities. A particular firmware may reject a setting or report a different value; acknowledgement alone is never treated as a verified change.

| Receiver PID (VID `1532`) | Source identity | GET transaction | SET phase `00` | SET phase `01` |
| --- | --- | --- | --- | --- |
| `009F` | Viper Mini Signature Edition wireless | `1F` | `1F` | `1F` |
| `00C1` | Viper V3 Pro wireless | `1F` | `1F` | `1F` |
| `00BF` | DeathAdder V4 Pro wireless | `1F` | `1F` | `1F` |
| `00B3` | Generic HyperPolling Wireless Dongle; paired model unknown | `1F` | `1F` | **`FF`** |

High-rate GET uses class `00`, command `C0`, declared request size `01`. The rate is returned in argument 1. The parser accepts a declared response size of `01` or `02` in a complete, validated 91-byte feature report; it does not truncate the report to the declared size. SET uses command `40`, size `02`, with the phase in argument 0 and rate in argument 1:

| Rate (Hz) | Encoding |
| --- | --- |
| 125 | `40` |
| 500 | `10` |
| 1000 | `08` |
| 2000 | `04` |
| 4000 | `02` |
| 8000 | `01` |

The source sends SET once with phase `00`, then once with phase `01`, using the transactions above. The precise phase meaning is not established; upstream calls the parameter “maybe varstore.” This implementation does not describe the phases as verified persistent banks or claim that one phase alone is sufficient. The sequence belongs to one deliberate Apply action, rather than an automatic retry.

## Receiver association and verification

The generic `00B3` path retains its receiver-scoped identity, `<receiver>:razer:00B3`, with model ID `1532:00B3`. It does not infer a paired mouse model or unique peripheral identity from the dongle name, a serial string or a successful configuration reply. Its paired model and radio-link state remain explicitly unknown. Battery monitoring still returns Unsupported for this receiver without sending battery protocol commands; adding polling control does not manufacture a battery percentage or connection state.

Each operation binds the selected receiver ID, source model ID, device identity, transport path and descriptor. Composite paths must use interface `00`. The 90-byte Razer message includes a leading Windows report ID `00`, producing 91 bytes; replies require matching transaction, class, command, framing, checksum, declared size and successful status. Acknowledgements are validated on these fields, without assuming that their argument bytes echo the setting. Polling transactions are separate from battery-query transactions.

Controls start disabled. Detect reads configuration without changing it. Apply freshly reads the current rate and validates the requested source-profile option. An unchanged ordinary rate needs no setting write. A high-rate Apply always sends both required phases, even when GET already reports the selected rate: GET does not read the phases independently, and an earlier partial attempt may already have changed its reported value. Before **each** setting command, the controller rechecks cancellation and the caller's current opt-in/selection guard. It validates the first acknowledgement before sending the second command. A failure in the first phase stops the sequence; a timeout ends the operation without reusing the session.

After both high-rate acknowledgements, a fresh GET must report the requested rate before the configuration is marked verified. This establishes the reported effective configuration, not the independent contents or persistence of both phases, a measured USB reporting frequency, or a live peripheral connection. Mismatched readback is shown as unverified with the actual reported rate.

If cancellation or failure occurs after a setting command begins, the setting may already have changed or the two-command sequence may be incomplete. The result stays explicitly unverified. Detect again before another attempt; a deliberate Apply of the same high rate can resend the complete sequence. This is a new manual request, not an automatic retry. No automatic retry, rollback, pairing, firmware change, lighting change, startup restore or saved pending rate is sent. Battery monitoring remains independent and read-only.

Compilation, automated tests, application runtime and physical hardware verification remain separate. Tests use synthetic reports and fake transports. Windows feature routing, real reporting frequency, firmware persistence, vendor-software coexistence and actual receiver/peripheral compatibility remain unverified. Obtain permission before application runtime or hardware testing.
