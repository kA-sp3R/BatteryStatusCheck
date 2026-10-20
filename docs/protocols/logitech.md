# Logitech receiver battery support

Implemented, **not verified on physical hardware**. Compiling or passing synthetic protocol tests does not establish compatibility with any particular mouse or firmware.

## Selected transport and association

Only Logitech USB VID `046D`, HID++ vendor usage page `FF00`, usage `0001`/`0002`, and the following receiver product IDs are selected:

| Receiver family | Product IDs |
| --- | --- |
| Unifying | `C52B`, `C532` |
| LIGHTSPEED | `C539`, `C53A`, `C53D`, `C53F`, `C541`, `C545`, `C547`, `C54D` |

Bluetooth and directly attached USB mice are deliberately excluded. A product family name does not establish that its receiver uses one of these IDs or that its firmware exposes a supported battery feature. G PRO X SUPERLIGHT, G502, G703, G903, G305 and productivity mouse support must be confirmed with their actual receiver, feature set and firmware; no blanket model compatibility claim is made.

Receiver-information register `0x2B5` (read command `83 B5`) subregister `03` supplies the slot limit when supported. Pairing subregisters `20 + slot - 1` identify populated slots; extended pairing subregisters `30 + slot - 1` supply peripheral serials. Unifying slots are used only when pairing metadata can be read. Receivers lacking these metadata use a bounded single-slot LIGHTSPEED fallback rather than sweeping arbitrary device addresses.

All peripheral commands target their receiver slot (`1`–`6`), never the receiver address `FF`. A fresh root-feature ping, with a random echoed marker, is required before reading battery fields. Pairing information and an enumerated receiver alone never establish connection. Identities include receiver identity, slot and device-reported WPID/serial or unit ID when available. No pairing, receiver notification configuration, profile, or polling-rate changes are sent.

## Percentage and status rules

| Protocol | Source | Behavior |
| --- | --- | --- |
| HID++ 2+ | `1004` unified battery | Query capabilities first. Report a percentage only when flags byte bit `02` confirms state-of-charge support. Preserve device-reported `0%`. Otherwise retain unavailable percentage; never translate level categories to numbers. |
| HID++ 2+ | `1000` battery status | Require GetBatteryCapability to advertise mileage (flags bit `02`, at least ten levels), rather than interpreting a category value as a percentage. Without mileage, preserve charging but no percentage. A confirmed mileage/discharging `0%` is valid. Zero in a charging/full state can mean unavailable; do not manufacture `100%` from a full flag. |
| HID++ 1 | Register `0D` battery charge | Use the returned percentage, including `0%`, and supported charging flags. |
| Other | Register `07`, `1001` voltage, unknown features | Percentage unsupported. No voltage curve or category estimate is supplied. |

Feature indices are discovered through `0000` GetFeature. Optional `0003` and `0005` features supply unit ID and peripheral name; no model name is inferred from a receiver name. Replies must match report format, slot, feature/register, function and HID++ software ID. Pairing queries additionally match their echoed subregister. Ping also matches its random marker. Unrelated reports are ignored with a 700 ms / 64-packet bound. HID++ 1.0 register transactions lack software IDs in the protocol and cannot provide the same correlation strength as feature calls; simultaneous vendor software may cause failures.

Explicit legacy connection-request-failed / unknown-device errors yield disconnected. Resource errors, I/O errors, malformed replies and timeouts yield failed/unknown connection, not a fabricated disconnected state. Prior successfully sampled values may be retained by the monitor only as stale, with their original timestamp.

## Research sources and licences

This C# implementation was written independently from the protocol facts below. No source code was copied or translated from these projects, and their libraries are not bundled.

- [Logitech's root feature specification](https://github.com/Logitech/cpg-docs/blob/f9107a4e3190d798f9be8c5d596f255f5e5741d2/hidpp20/features/0x0000-IRoot.rst), commit `f9107a4e3190d798f9be8c5d596f255f5e5741d2`: feature discovery, protocol-version ping and echoed marker.
- [Solaar](https://github.com/pwr-Solaar/Solaar/tree/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver), GPL-2.0-or-later, commit `e7304c4c451cc9bb4f206a914844525e67856a28`: `base_usb.py` receiver IDs, `receiver.py` pairing fields, `base.py` framing/correlation and legacy ping errors, `hidpp10.py` register `0D`, `hidpp20.py` battery/name/identity fields.
- [Linux HID++ driver](https://github.com/torvalds/linux/blob/c03c1a14507dfaf8b856c2608f9ac216021fc222/drivers/hid/hid-logitech-hidpp.c), GPL-2.0-only: `1004` state-of-charge capability flag; `1000` mileage capability (level count and flags), zero-as-unknown in charging states and status mapping.
- [libratbag](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c), MIT, commit `8235b5bb6032dea15901ab58a8218956f4494d08`: `1000` battery-level fields and charging status values.
- [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer/blob/f0e983e88900592237c7df9ae6b80b9dbeb3020b/battery.py), reviewed as a secondary reference. Its broader probing and numeric estimates were not adopted.

No runtime or physical hardware test has been performed. Those checks require the user's permission and Windows hardware access.
