# Razer wireless receiver research

This record describes independently implemented protocol facts for battery association and manual polling. Source inspection is software evidence. No application or vendor code was launched, no physical receiver was queried and no battery accuracy, USB timing or firmware persistence was measured. Runtime and hardware work is recorded in [deferred verification](../deferred-verification.md).

## Sources and provenance

The [evidence manifest](razer-native-evidence.json) records manufacturer URLs, SHA-256 hashes, explicit dongle metadata and control interfaces. Manufacturer JavaScript was inspected as static text; it is not executed, copied into the implementation or redistributed. Metadata establishes a specific source-described transport path, not blanket physical compatibility or admission of every USB/Bluetooth form.

| Evidence | Pinned source | Use |
| --- | --- | --- |
| OpenRazer | [`477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`](https://github.com/openrazer/openrazer/tree/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d) | Battery/charging, wireless PIDs, interfaces, polling commands and transactions |
| secbird | [`95dd41f70d91f97f4b214717b3d6bd632757e72a`](https://github.com/infinityInfinite/secbird/commit/95dd41f70d91f97f4b214717b3d6bd632757e72a) | Modern `00:BF`, size 80, count/connection/big-endian PID records |
| Published HyperPolling captures | [`042622186887a45a2aade6c2673279717bb0dfb9`](https://github.com/ginzberg/razer-hyperpolling-captures/commit/042622186887a45a2aade6c2673279717bb0dfb9) | Legacy `BF` framing and connected/disconnected/empty records |

Relevant OpenRazer files are GPL-2.0-or-later; secbird is MIT/Apache-2.0. Code was not transplanted. No licence was found for the capture repository, so captures are not bundled or redistributed; only independently recorded protocol observations are used.

The primary protocol source is [product 179 middleware `6120.ef5d145fafdfd0069d53.js`](https://apps.razer.com/synapse/products/179/mw/6120.ef5d145fafdfd0069d53.js), SHA-256 `1bc185f4b6407b0c5aab6939b827a9f3ccba88e21966c222607bc0e92742beb8`. It defines device lists, primary roles, connection queries and DualLink namespaces. Product 179's [main bundle](https://apps.razer.com/synapse/products/179/mw/main.6d1e356030ef563555d4.js), SHA-256 `b836abc03ea2b5eaec54e8bdfc48d06d5a1bab0bcd0b38499fa1a3c97ecae429`, supplies associated platform/transport code.

## Read-only receiver commands

These commands use a 90-byte Razer message inside a Windows feature report with raw report ID `00`. Software validates the complete 91-byte reply, checksum, transaction, class, command, framing and successful declared payload length before parsing values.

| Class / command | Declared size | Observed meaning |
| --- | --- | --- |
| `07 / 80` | `02` | Battery raw value in argument 1, scaled 0–255 to 0–100%; real zero remains 0% |
| `07 / 84` | `02` | Charging in argument 1: 0 discharging, 1 charging, other unknown |
| `00 / BF` | `50` | Count, then records: link flag, PID high, PID low |
| `00 / C5` | `03` | Primary type, then big-endian PID; mouse type 1, keyboard type 2 |
| `00 / B8` | `01` | Connection byte: 0 disconnected, 1 connected, other unknown |

Modern generic HyperPolling discovery uses transaction `1F`. Older captured `BF` requests use transaction `FF`, size `31` and argument 0 = `10`. Captures distinguish connected `00A6` (`count=1, link=1, PID=00A6`), paired-disconnected (`count=1, link=0, PID=00A6`) and an empty slot (`count=1, link=0, PID=FFFF`). The implementation permits legacy discovery only after an explicit Unsupported modern reply on generic `00B3`. Malformed data, timeout, busy exhaustion or another failure never justifies switching profiles.

Manufacturer DualLink separates transaction namespaces for mouse (`00`), keyboard (`80`) and Linker (`E0`), combined with a sequence counter. Software selects `01`, `81` and `E1`. Mouse/keyboard routing follows type, not primary/secondary order. Legacy battery transactions (`FF`, `3F`, `1F`, `9F`) remain separate from these optional discovery/routed namespaces.

`BF` provides native PID and reported link evidence, not a serial. Invalid flags, duplicate PIDs, invalid identities and truncated records are rejected; disconnected `FFFF` is an empty slot. `C5` identifies the primary role; explicit catalog knowledge can establish a secondary mouse or keyboard. Unknown secondary roles, conflicting types and peers sharing an unaddressed namespace are refused. An unknown single child requires role evidence rather than a guessed mouse type. Device-list/role checks around reads discard measurements whose association changes. `B8` is an independent fallback link query when ordinary modern discovery explicitly reports Unsupported. A disconnected reply discards the legacy value; unknown link or failed optional discovery does not manufacture a disconnect or replace a genuine zero.

No device-side battery sample timestamp or proof of a new physical measurement is exposed. A validated battery reply with matching reported association cannot establish that firmware did not cache it. PID alone cannot uniquely distinguish same-model replacement. Status/detail text and deferred verification preserve these limits.

## Audited master receiver factories

These records explicitly identify a receiver/dongle and control interface. They justify read-only associated-child discovery, not a battery measurement for the dock itself.

| Receiver | PID / interface | Official factory | SHA-256 |
| --- | --- | --- | --- |
| Mouse Dock Pro | `00A4` / `00` | [product 164](https://apps.razer.com/synapse/products/164/mw/main.62e270f8980e6cf490a5.js) | `51cd498c2a9e993d6d572f2818f63bed9b92402ce04b268f3a9a8da87ad77ded` |
| HyperFlux V2 | `00CF` / `00` | [product 207](https://apps.razer.com/synapse/products/207/mw/main.35716b945eedcc2ed000.js) | `6144428cdb26dd7d7696ac689f599f5f3d818aef9521a9e188b9a57e9f35c2bc` |
| Mouse Dock V2 Pro | `00F1` / `03` | [product 241](https://apps.razer.com/synapse/products/241/mw/main.22fef509d930e6fcd701.js) | `75c75478f6012a5b0de906c1bcea4cdb24c2d0038b338515e7a896fd16ef67f6` |

Factories select Linker or multi-device Linker and the `E0 | sequence` namespace. Software uses `E1` for discovery, then proven mouse/keyboard namespaces for associated battery reads. Dock polling writes remain disabled without a documented setting-command profile.

## Interface number is not report ID

Native middleware's `sendCommand` defaults `reportId` to zero and carries `claimInterface` separately for its 90-byte feature payload. Its WebUSB path issues SET_REPORT value `768` (`0x0300`: feature report, ID zero), with `claimInterface` as interface index. The bridge's `connectHidDevice` query-data prefix varies by platform and can contain `claimInterface`; bridge metadata is not proof that the physical report ID equals the interface number.

In the pinned middleware text, observations occur near character offsets 814928 (`reportId` default), 821554 (separate interface/report ID send payload), 817879 (WebUSB SET_REPORT) and 816292 (bridge prefix). The pinned main bundle exports the platform helper near 111372 and detects Windows near 113961. These are navigation aids for the exact hashed files, not runtime traces. Interface `03` therefore retains raw report ID `00`; actual Windows routing still requires separately authorized physical verification.

## Coverage and verification boundary

The catalog contains 47 profiles: 37 mouse paths, six keyboard paths and four masters including HyperPolling. Eighteen additional mouse paths were admitted only where wireless USB PID and command profile were explicit. Wired/Bluetooth companions and ambiguous old shared IDs remain excluded. Inspected Huntsman families are wired USB forms without the wireless battery interface, so their absence is outside 2.4 GHz scope rather than missing wireless support. A generic DualLink declaration alone does not admit an otherwise unproven wireless form.

The [battery protocol](../protocols/razer.md) and [polling protocol](../protocols/razer-polling.md) describe software behaviour. Fake transports and synthetic frames cover parsing, genuine zero, fallback boundaries, association changes, roles, connection handling and write guards. [Verification](../verification.md) records Windows CI separately. Physical compatibility, sleeping/firmware-cache behaviour, battery accuracy, reporting frequency, persistence and vendor-software coexistence remain unmeasured and deferred; a source match or successful synthetic test does not imply them.

The [requested reference comparison](reference-projects.md) records the later Viper V4 Pro `00E6` admission, explicit mouse type on interface 03, and source-backed slow-receiver timing correction.
