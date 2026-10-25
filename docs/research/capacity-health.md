# Capacity-health protocol evidence

Research snapshot: 23 October 2026. This investigation inspected the existing protocol readers, pinned upstream source and the public HID++ specification as text. No reference program, vendor API, driver, capture helper or physical device was executed. The capacity calculation is implemented independently; manufacturer health percentages are not used.

## Current device support

**No currently admitted physical device has evidence-backed capacity-health support in this application.** The inspected receiver paths provide useful state of charge, categories or charging information, but not the capacity/current/power telemetry needed for this feature. This is a limit of the documented paths inspected here, not proof that every firmware has no other command.

| Family and supported path | Documented data | Capacity-health decision |
| --- | --- | --- |
| Razer mapped receivers, including HyperPolling and routed mouse/keyboard children | Class `07`, command `80` returns a charge-level byte `0..255`; `07:84` returns charging state. Receiver commands establish model/link/role association. | A charge-level byte is not mAh, Wh, current or power. No supported design/FCC/current/power field or independently documented full-to-empty measurement boundaries were found. Model association also does not uniquely identify the physical battery. Show **Capacity measurement unavailable**. |
| Logitech HID++ 2 `1004` unified battery and `1000` battery level status | Capability-gated state-of-charge percentages or native categories, charging/status and, for `1000`, next battery level. | “Mileage” enables a reported state-of-charge percentage; it is not a measured mAh/Wh capacity. Neither path supplies usable full-charge capacity or a discharge current/power trace. Preserve genuine percentages/categories and show **Capacity measurement unavailable**. |
| Logitech HID++ 1 battery registers `0D`/`07` | Percentage or native category and charging flags. | No supported full-charge/design capacity or discharge telemetry. Show **Capacity measurement unavailable**. |
| Investigated Logitech `1001` battery voltage and `1F20` ADC measurement | Voltage in mV and charging/status flags. Upstream software may apply a voltage-to-level lookup. | Voltage alone does not measure discharged charge or energy. No voltage curve, assumed current or runtime multiplier is admitted. These features are not added as health probes. |
| Alienware Pro wireless headset, receiver `413C:A529`, vendor collection `FF13` | Battery opcode `0A` documents percentage at byte 6 and charging at byte 7; identity and link commands establish model/radio state. | Other battery-reply bytes remain undocumented. They are not reinterpreted as mAh, voltage, current or FCC. No unique battery identity or independently documented full/empty capacity boundaries are available. Show **Capacity measurement unavailable**. |

No model-specific design-capacity constants are introduced. A marketing claim such as “up to N hours” is not a design-capacity specification. A receiver model, mouse name or firmware version also cannot establish which battery or replacement cell is installed. A user can record a design value from an actual specification or label with its source, units and measurement basis; that input alone cannot make capacity or health measurable.

## Evidence and licences

These sources establish field semantics; external authors' hardware observations are not this application's hardware verification. No upstream implementation, voltage table or binary is copied.

- [OpenRazer `477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d`, `driver/razerchromacommon.c`](https://github.com/openrazer/openrazer/blob/477f4d6d3ab7e9deb6310d5bd3742560c5cbe80d/driver/razerchromacommon.c): `razer_chroma_misc_get_battery_level` declares `0..255` in argument 1 and request `07:80`, size `02`; charging is `07:84`. GPL-2.0-or-later source inspected as a protocol reference.
- [razer-battery-report `4abf90c280a35fe9c451c17589bedaf237204ec9`, `crates/razer-hid/src/report.rs`](https://github.com/xzeldon/razer-battery-report/blob/4abf90c280a35fe9c451c17589bedaf237204ec9/crates/razer-hid/src/report.rs): corroborates battery/charging command payload semantics. MIT source inspected; GitHub blob `239c21af4bfe4964193fd13ae477aab0298dbeb2` was read at the pinned revision. Existing association evidence is retained in [Razer wireless research](razer-wireless.md).
- [Logitech HID++ 2.0 draft specification, 4 June 2012](https://drive.google.com/file/d/0BxbRzx7vEV7eU3VfMnRuRXktZ3M/view), pages 9–10: `1000` battery level, next level, status, capability flags and percentage mileage. Existing downloaded PDF SHA-256: `c3d642248df91ee2d00420c359e4693ea36384b63d308abbc45bb4838919e58b`. These are level/capability fields, not capacity in mAh or Wh.
- [Linux HID++ driver `c03c1a14507dfaf8b856c2608f9ac216021fc222`](https://github.com/torvalds/linux/blob/c03c1a14507dfaf8b856c2608f9ac216021fc222/drivers/hid/hid-logitech-hidpp.c): `1004` SOC/category flags, `1001` big-endian voltage/status and `1F20` ADC voltage/status decoding. GPL-2.0-only source inspected; voltage-to-level estimation is not adopted.
- [Solaar `e7304c4c451cc9bb4f206a914844525e67856a28`, `hidpp20.py`](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver/hidpp20.py): `decipher_battery_status`, `decipher_battery_unified`, `decipher_battery_voltage` and `decipher_adc_measurement` corroborate the distinction between charge level and voltage. Its battery/voltage decoders expose no usable full-charge or design capacity. GPL-2.0-or-later source inspected without copying code or lookup tables.
- [awpro `d92f6df573b0a5b4c5551fcfc8d3311c435e7a88`, `src/protocol.rs`](https://github.com/romain-ncls/awpro/blob/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88/src/protocol.rs): `parse_battery` and `parse_charging` identify the two documented battery fields. [Earlier AWCC research](alienware-awcc.md) records source licence/attribution, capture-derived evidence and the lack of an individual battery identity. Unknown trailing bytes are not a capacity specification.

## Admission rules for capacity measurements

A future protocol adapter can supply independently documented capacity telemetry through the health subsystem. It must identify the physical battery sufficiently to avoid assigning another battery's history to it, document units/sign/scale and measurement basis, and validate its replies through the normal read-only monitoring path. This research does not enable extra commands or a vendor health API.

There are two admissible full-capacity routes:

1. **Device-reported usable full-charge capacity.** A field must explicitly describe the battery's usable *full-charge* capacity with units and basis. It can be observed while current charge is partial because the field still describes full capacity. A remaining-capacity field cannot be relabelled as full capacity or scaled by a percentage.
2. **Calculated usable capacity from discharge telemetry.** Integrate measured discharge current or power over a complete, continuous, supported full-to-empty discharge interval. Full/empty boundaries must come from documented telemetry semantics, not from seeing 100%/0% or waiting a nominal runtime. A partial discharge is coverage information, not a full-charge measurement.

For current samples in mA, trapezoidal integration is:

`usable mAh = Σ ((I[n-1] + I[n]) / 2) × Δt_hours`

For measured power samples in W:

`usable Wh = Σ ((P[n-1] + P[n]) / 2) × Δt_hours`

Only same-unit capacities on the same basis may be compared:

`health (%) = usable full-charge capacity / design capacity × 100`

Battery-terminal Wh and downstream load/output Wh are different bases. mAh and Wh are different units. Nominal voltage is not permission to convert between them, infer power or correct unspecified conversion losses. Percentage, voltage alone, elapsed runtime alone and assumed power draw are never inputs to these capacity calculations. A ratio above 100% can reflect the actual inputs and specification tolerance; it must not silently be clamped to a manufacturer-style health value.

## Continuity, history and completion boundaries

An active integrated cycle must be rejected after a missing/late/duplicate or out-of-order sample, uncertain clock interval, charging, disconnection/reconnection, sleep/resume, telemetry loss, unit/basis change or uncertain/changed battery identity. No missing area is filled with an assumed current or extrapolated from percentage. A restart must not join a partial cycle to later samples.

Records distinguish device-reported full capacity, calculated complete usable capacity, remaining capacity and incomplete/rejected coverage. They retain observation/measurement times, sample coverage, method, source, interruptions and identity context. Valid completed history can survive a restart, but persistence is not evidence that a newly connected same-model device is the same battery. Unsafe identities must not automatically acquire old health results.

For today's admitted receivers the normal result is explicit: **Capacity measurement unavailable — no supported usable full-charge-capacity field or measured discharge current/power with trustworthy boundaries and battery identity.** A saved design specification may still be displayed, together with its source and units. Software tests can validate arithmetic, continuity rejection, persistence and presentation using synthetic telemetry; they cannot certify physical battery accuracy or add a model to the hardware-supported list.
