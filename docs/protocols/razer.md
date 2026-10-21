# Razer battery and receiver association

The software implements 46 explicit USB 2.4 GHz endpoint profiles: 36 mouse paths, six keyboard paths and four receiver masters. This includes 18 additional source-documented mouse paths and the Mouse Dock Pro, HyperFlux V2 and Mouse Dock V2 Pro masters. These are source-backed implementations, not a hardware-tested compatibility list. The complete mapping is maintained in [RazerDeviceCatalog.cs](../../src/DeviceBatteryStatus.Core/Protocols/RazerDeviceCatalog.cs).

Protocol facts were independently implemented from [OpenRazer `477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d), static manufacturer Synapse middleware and published receiver captures. Relevant OpenRazer files are GPL-2.0-or-later; source was inspected, not copied or redistributed. Manufacturer code was inspected as text, never executed or included in this repository. Exact sources, hashes and interpretation limits are recorded in [Razer wireless research](../research/razer-wireless.md) and its [evidence manifest](../research/razer-native-evidence.json).

## Framing and values

A Windows feature report contains report ID `00` followed by a 90-byte Razer message. The selected collection must support a 91-byte feature report. XOR covers message offsets 2–87, corresponding to Windows-report offsets 3–88; the checksum is at Windows offset 89. Replies must match transaction, class, command, framing, payload length and checksum. Busy replies receive bounded retries. A timeout ends the affected session's further queries.

Battery uses class `07`, command `80`, payload length `02`. Argument 1 is the reported 0–255 value, converted to 0–100%. A validated zero is **0%**. Charging uses class `07`, command `84`, payload length `02`: argument 1 of `0` means discharging, `1` means charging and other values remain unknown. A charging failure preserves an independently completed battery reading with unknown charging state. Profiles without a documented charging query skip it; missing charging support never becomes an invented discharging state.

The protocol provides no battery sample timestamp or nonce. `Current` means a successful report was received during this observation; it does not prove firmware sampled its physical battery at that moment. Unknown link state can accompany a genuine reported percentage. `Disconnected`, `Unsupported` and `Failed` are separate outcomes. The monitoring service retains previous successful samples as stale after uncertain failures, preserving the original observation time.

## Endpoint selection

Only catalogued VID `1532` endpoint PIDs are admitted. Unknown USB PIDs, wired counterparts and Bluetooth paths are excluded. A native child PID discovered through an admitted receiver can still be displayed without admitting that PID as a USB endpoint. An unknown child is named `Razer wireless peripheral (1532:XXXX)`, without guessing its model or type.

The following are legacy battery transactions. Routed DualLink reads use separate namespaces described below.

| Endpoint PIDs | Models | Battery transaction | Control interface |
| --- | --- | --- | --- |
| `0025`, `003F`, `0045` | Mamba 2012; Naga Epic Chroma; Mamba Chroma | `FF` | `00` |
| `005A`, `0072`, `007D` | Lancehead; Mamba Wireless; DeathAdder V2 Pro | `3F` | `00` |
| `007B`, `0083` | Viper Ultimate; Basilisk X HyperSpeed | `FF` | `00` |
| `0062`, `006F`, `0077` | Atheris; Lancehead Wireless; Pro Click | `1F` | `00` |
| `0088`, `0090`, `0094`, `009A`, `009C` | Basilisk Ultimate; Naga Pro; Orochi V2; Pro Click Mini; DeathAdder V2 X HyperSpeed | `1F` | `00` |
| `009F`, `00A6`, `00A8`, `00AB`, `00B0`, `00B4` | Viper Mini Signature Edition; Viper V2 Pro; Naga V2 Pro; Basilisk V3 Pro; Cobra Pro; Naga V2 HyperSpeed | `1F` | `00` |
| `00B7`, `00B8`, `00B9`, `00BF`, `00C1`, `00C3`, `00C5` | DeathAdder V3 Pro; Viper V3 HyperSpeed; Basilisk V3 X HyperSpeed; DeathAdder V4 Pro; Viper V3 Pro; alternate DeathAdder V3 Pro; DeathAdder V3 HyperSpeed | `1F` | `00` |
| `00C8`, `00CD`, `00D1`, `00D4`, `00D7`, `00DB`, `00DF` | Pro Click V2 Vertical Edition; Basilisk V3 Pro 35K; Pro Click V2; Basilisk Mobile; Basilisk V3 Pro 35K Phantom Green Edition; Cobra HyperSpeed; Viper V3 Pro SE | `1F` | `00` |
| `025C`, `0290`, `0296`, `02D5` | BlackWidow V3 Pro; DeathStalker V2 Pro; DeathStalker V2 Pro TKL; BlackWidow V4 Tenkeyless HyperSpeed | `9F` | `02` |
| `0271`, `02BA` | BlackWidow V3 Mini HyperSpeed; BlackWidow V4 Mini HyperSpeed | `9F` | `03` |

The four master profiles identify receivers, not the dock's own battery or an inferred paired model:

| Receiver PID | Receiver | Discovery transaction | Control interface |
| --- | --- | --- | --- |
| `00B3` | HyperPolling Wireless Dongle | `1F` | `00` |
| `00A4` | Mouse Dock Pro | `E1` | `00` |
| `00CF` | HyperFlux V2 | `E1` | `00` |
| `00F1` | Mouse Dock V2 Pro | `E1` | `03` |

Composite Windows HID paths must have the matching case-insensitive `&mi_XX` interface component. Non-composite paths without an MI component are accepted. Interface number and report ID are distinct: interface `03` still uses raw report ID `00`. Static manufacturer transport evidence supports this distinction; actual Windows routing remains physically unverified.

The 18 added mouse paths are `0025`, `003F`, `0045`, `005A`, `0062`, `006F`, `0072`, `0077`, `0090`, `0094`, `009A`, `00A8`, `00B4`, `00C8`, `00CD`, `00D1`, `00D4` and `00D7`. Source ambiguity is not resolved by admitting wired companions. The inspected Huntsman family entries are wired USB devices without the wireless battery interface; they are **outside this project's 2.4 GHz scope**, not an unfinished wireless implementation.

## Device lists, roles and connection

Class `00`, command `BF`, declared size `50` hex returns a count followed by `(connection, PID high, PID low)` records. Connection `0` means paired but disconnected; `1` means connected. Invalid flags, duplicate PIDs, invalid identities, truncated records, mismatched headers and checksum failures are rejected. A disconnected `FFFF` record is an empty slot, not a peripheral.

For `00B3`, only an explicit Unsupported reply to modern `BF` permits the documented legacy request: transaction `FF`, size `31`, argument 0 = `10`. Timeout, malformed data or another failure never triggers this fallback. Ordinary receivers use modern discovery with transaction `01` for mouse profiles or `81` for keyboard profiles, without legacy `FF` fallback. Dock masters use `E1`.

A single active HyperPolling mouse can have its targetless battery reply bound to its native PID through device-list checks around the read. A changed, missing or ambiguous child causes the affected value to be discarded. Unknown roles are not guessed from the HyperPolling name: an unknown single child requires primary-device role evidence, while a known catalog child supplies an explicit type. Child identity is `<receiver>:razer:<receiverPID>/wireless/<childPID>`, with model ID `1532:<childPID>`. This distinguishes a changed PID, but does not provide a unique serial or detect replacement by another physical device of the same model.

Shared receivers and known keyboard children use class `00`, command `C5`, size `03` to read primary type and big-endian PID. Mouse type `1` and keyboard type `2` select manufacturer namespaces: battery transaction `01` for a mouse and `81` for a keyboard, independent of primary/secondary ordering. A secondary role is inferred only from an explicit known catalog profile. Unknown secondary roles, inconsistent types and multiple peers requiring the same unaddressed namespace are refused without attributing a battery value. Device lists and primary roles are checked around routed reads. A narrow single-peer legacy fallback is allowed when `C5` explicitly reports Unsupported and the peer PID equals the known endpoint PID.

Ordinary profiles first obtain their documented legacy battery sample. If modern `BF` explicitly reports Unsupported, class `00`, command `B8`, size `01` can independently establish connection: argument 0 of `0` discards the battery value and reports Disconnected; `1` retains the genuine value and reports Connected; other bytes retain the genuine value with unknown link state. Failed optional discovery/link probes preserve the completed legacy report with unknown connection, and timed-out sessions are not reused. Charging failures likewise do not erase a successful battery sample.

An empty successful device list reports Disconnected. A plugged-in receiver alone never proves a connected peripheral. Successful link and role checks establish reported association, but cannot certify that firmware did not cache a value while a device slept. Receiver removal and association changes are handled separately by the monitoring service; uncertain partial batches do not falsely declare every omitted child disconnected.

## Verification boundary

Battery monitoring sends read commands only; it never pairs devices or writes firmware, lighting or configuration. Deliberate polling changes use the separate [manual polling controller](razer-polling.md).

Windows CI compilation and automated tests are tracked in [verification](../verification.md). Synthetic protocol and transport tests are software evidence. Application runtime, physical receiver/firmware compatibility, sleeping-device behaviour, actual battery accuracy and same-model replacement behaviour remain deferred in [the verification record](../deferred-verification.md). They were not measured by this investigation and are not claimed as passing.
