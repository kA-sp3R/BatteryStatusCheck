# Razer polling-rate control

The manual controller implements 37 explicit USB 2.4 GHz receiver profiles: 33 ordinary-rate paths and four high-rate paths. Protocol facts were independently implemented from [OpenRazer `477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d), especially `razerchromacommon.c`, the mouse driver's polling paths and daemon model mappings. Relevant upstream files are GPL-2.0-or-later; implementation was inspected, not transplanted. [Manufacturer receiver research](../research/razer-wireless.md) supplies independent association commands.

Only documented receiver paths with the correct interface and selected identity are admitted. Wired and Bluetooth paths, keyboards and unknown USB endpoint PIDs are excluded. Mouse Dock Pro `00A4`, HyperFlux V2 `00CF` and Mouse Dock V2 Pro `00F1` have battery association support, but no polling writes are enabled without a proven setting-command profile.

## Ordinary HyperSpeed receivers

The ordinary profile offers **125, 500 and 1000 Hz**. An optional HyperPolling upgrade does not add high-rate commands to an ordinary receiver path.

| Receiver PID (VID `1532`) | Model | GET / SET transaction |
| --- | --- | --- |
| `0025`, `003F` | Mamba 2012; Naga Epic Chroma dock | `FF` / `FF` |
| `0045` | Mamba Chroma | **`FF` / `3F`** |
| `005A`, `006F`, `0072` | Lancehead; Lancehead Wireless; Mamba Wireless | `3F` / `3F` |
| `0062`, `0077` | Atheris; Pro Click | `1F` / `1F` |
| `007B` | Viper Ultimate | `FF` / `FF` |
| `007D` | DeathAdder V2 Pro | `3F` / `3F` |
| `0083` | Basilisk X HyperSpeed | `FF` / `FF` |
| `0088` | Basilisk Ultimate | **`FF` / `1F`** |
| `0090`, `0094`, `009A`, `009C` | Naga Pro; Orochi V2; Pro Click Mini; DeathAdder V2 X HyperSpeed | `1F` / `1F` |
| `00A6`, `00A8`, `00AB`, `00B0`, `00B4` | Viper V2 Pro; Naga V2 Pro; Basilisk V3 Pro; Cobra Pro; Naga V2 HyperSpeed | `1F` / `1F` |
| `00B7`, `00B8`, `00B9`, `00C3`, `00C5` | DeathAdder V3 Pro; Viper V3 HyperSpeed; Basilisk V3 X HyperSpeed; alternate DeathAdder V3 Pro; DeathAdder V3 HyperSpeed | `1F` / `1F` |
| `00C8`, `00CD`, `00D1`, `00D4`, `00D7`, `00DB`, `00DF` | Pro Click V2 Vertical Edition; Basilisk V3 Pro 35K; Pro Click V2; Basilisk Mobile; Basilisk V3 Pro 35K Phantom Green Edition; Cobra HyperSpeed; Viper V3 Pro SE | `1F` / `1F` |

This adds the same 18 audited mouse paths listed in the [battery protocol](razer.md). Battery and polling transactions are independent: `006F`, for example, uses battery transaction `1F` and polling transaction `3F`.

Ordinary GET uses class `00`, command `85`, declared size `01`; SET uses command `05`, size `01`. Argument 0 encodes `08` = 125 Hz, `02` = 500 Hz, `01` = 1000 Hz. There is no proven ordinary 250 Hz encoding in this path. Unsupported values are rejected without rounding or substituting a setting.

## High-rate receivers

The high-rate profile offers **125, 500, 1000, 2000, 4000 and 8000 Hz**, following exact OpenRazer profiles. These are static source-documented options, not device-advertised or negotiated capabilities. Firmware can reject a request or return another rate.

| Receiver PID (VID `1532`) | Source identity | GET transaction | SET phase `00` | SET phase `01` |
| --- | --- | --- | --- | --- |
| `009F` | Viper Mini Signature Edition wireless | `1F` | `1F` | `1F` |
| `00C1` | Viper V3 Pro wireless | `1F` | `1F` | `1F` |
| `00BF` | DeathAdder V4 Pro wireless | `1F` | `1F` | `1F` |
| `00B3` | HyperPolling Wireless Dongle | `1F` | `1F` | **`FF`** |

High-rate GET uses class `00`, command `C0`, declared request size `01`. The rate is returned in argument 1. The parser accepts declared response size `01` or `02` in a complete, validated 91-byte report. SET uses command `40`, size `02`, with phase in argument 0 and rate encoding in argument 1:

| Rate (Hz) | Encoding |
| --- | --- |
| 125 | `40` |
| 500 | `10` |
| 1000 | `08` |
| 2000 | `04` |
| 4000 | `02` |
| 8000 | `01` |

The source sends phase `00` followed by phase `01`. Their precise meaning is not established; upstream calls the parameter “maybe varstore.” The software does not claim they are verified persistent banks or that one command is sufficient. Both commands belong to one deliberate Apply action.

## Selected identity and association guards

Each operation binds receiver ID, source model ID, selected identity, transport path and descriptor. Composite paths must use interface `00`. The Windows report has raw report ID `00` plus the 90-byte message. Replies require matching transaction, class, command, framing, checksum, declared size and successful status. Acknowledgements are validated without requiring their arguments to echo the setting.

For a native child of HyperPolling `00B3`, the controller accepts canonical identity `<receiver>:razer:00B3/wireless/<childPID>`, model `1532:<childPID>`, only on that receiver. It freshly requires exactly one connected `BF` record with the selected PID before and after Detect, before each setting phase and after readback. Known keyboard profiles are refused. An unknown child PID additionally requires `C5` to identify that same PID as mouse type `1`; a model name is never guessed. Missing, changed or ambiguous associations, invalid reports and query failures stop the operation. No setting command follows a failed pre-write check.

The legacy receiver-scoped identity `<receiver>:razer:00B3`, model `1532:00B3`, remains available for explicit receiver configuration when paired identity is unavailable. Its detail states that model and radio link are unknown. A successful configuration reply does not create a battery measurement or peripheral identity. [Battery monitoring](razer.md) separately uses `BF`, `C5` and `B8` where applicable to report genuine device data.

Ordinary documented mouse receivers also accept a canonical associated child when its native PID equals the physical receiver PID. Fresh `BF` transaction `01` must report that sole connected peer, and `C5` transaction `01` must confirm mouse type `1` and the same PID. Only an explicit Unsupported `C5` reply permits the catalog-known same-model legacy fallback; timeout, failure and malformed data do not. Cross-model or unknown ordinary children and multiple connected peers remain unavailable for polling writes.

PID association cannot distinguish physical devices of the same model and is not a unique serial. Options still describe the receiver's command profile; they do not prove that a particular child supports every offered frequency.

## Apply and readback

Controls start disabled. Detect reads configuration without changing it. Apply freshly reads the current rate and validates the requested source-profile option. An unchanged ordinary rate needs no write. High-rate Apply always sends both phases even if GET already reports the selected rate: GET does not read the phases independently, and an earlier partial attempt may already have changed its reported value.

Before each setting command, the controller rechecks cancellation and the caller's current opt-in/selection guard. Child association checks are followed by another guard check before writing. The first acknowledgement is validated before the second command. A timeout ends the operation without reusing the session.

After the required acknowledgements, a fresh GET must report the requested rate and the child association must still match before configuration is marked verified. This verifies receiver-reported configuration; it does not measure physical USB reporting frequency or establish persistence. Mismatched readback is unverified and preserves the actual reported rate.

If cancellation or failure occurs after a command starts, the setting may have changed or the sequence may be incomplete. The result remains unverified. A new Detect or deliberate Apply is required; there is no automatic write retry, rollback, pairing, firmware change, lighting change, startup restore or saved pending rate. Battery monitoring remains read-only.

Compilation, automated tests, application runtime and physical verification are separate. Synthetic reports and fake transports cover software paths. Windows routing, real reporting frequency, persistence, vendor-software coexistence and receiver/peripheral compatibility remain deferred in [the verification record](../deferred-verification.md). CI status is tracked in [verification](../verification.md).
