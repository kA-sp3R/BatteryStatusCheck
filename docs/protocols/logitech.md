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

## Reported percentages, categories and status

| Protocol | Source | Behavior |
| --- | --- | --- |
| HID++ 2+ | `1004` unified battery | Query capabilities first. Report a percentage only when flags byte bit `02` confirms state-of-charge support. Preserve device-reported `0%`. Otherwise decode the highest category bit after masking with the advertised category mask: `01` Critical, `02` Low, `04` Good, `08` Full. No supported category bit means Unknown. |
| HID++ 2+ | `1000` battery status | Require GetBatteryCapability to advertise mileage (flags bit `02`, at least ten levels) before accepting a percentage. Otherwise decode the specification's categories: `1–10` Critical, `11–30` Low, `31–80` Good, `81–100` Full. An encoded `25` means Low; it is never displayed as `25%`. The specification defines level `0` as unknown, including in mileage mode. A reported charge-complete status may supply a Full category, never a manufactured `100%`. |
| HID++ 1 | Register `0D` battery charge | Use the returned percentage, including `0%`, and supported charging flags. |
| HID++ 1 | Register `07` battery status | If `0D` is unsupported, preserve the reported category: `1–2` Critical, `3–4` Low, `5–6` Good, `7` High; zero means Unknown and values above seven are malformed. High does not claim charge completion. Charging is decoded separately. |
| Other | `1001` voltage, `1F20` ADC, unknown features | No percentage estimate is supplied. Voltage/ADC fields require a host-side conversion curve and cannot establish a directly reported percentage. |

A valid category is a `Current` battery reading with a sample time and no percentage. The popup shows its label and hides the percentage bar. Percentage alerts and lowest-percentage selection ignore categories; a selected preferred category device keeps a neutral tray icon and its category tooltip. Failures retain categories only as `Stale`, with the original sample time; confirmed disconnection clears the value.

### Alternative percentage investigation

After a category-only `1004` response, discover `1000` and inspect its mileage capabilities. Prefer a valid directly reported percentage; otherwise retain the original category and sample time. An unsupported, malformed or failed optional query must not discard a successfully sampled category. A timeout stops the receiver session, and explicit disconnection invalidates the reading. If both features provide categories, retain the first known category; an alternative may replace Unknown with a known category.

HID++ 1 checks percentage register `0D` before category register `07`. Legacy registers are not probed on HID++ 2 merely to search for a percentage. No capability for a particular user's device is assumed without its feature responses. Newer Centurion `0104` SOC belongs to a different headset transport/association path and is not an alternative that can be queried blindly on these mouse receivers. No Centurion support is claimed.

Feature indices are discovered through `0000` GetFeature. Optional `0003` and `0005` features supply unit ID and peripheral name; no model name is inferred from a receiver name. Replies must match report format, slot, feature/register, function and HID++ software ID. Pairing queries additionally match their echoed subregister. Ping also matches its random marker. Unrelated reports are ignored with a 700 ms / 64-packet bound. HID++ 1.0 register transactions lack software IDs in the protocol and cannot provide the same correlation strength as feature calls; simultaneous vendor software may cause failures.

Explicit legacy connection-request-failed / unknown-device errors yield disconnected. Resource errors, I/O errors, malformed replies and timeouts yield failed/unknown connection, not a fabricated disconnected state. Prior successfully sampled values may be retained by the monitor only as stale, with their original timestamp.

## Research sources and licences

This C# implementation was written independently from the protocol facts below. No source code was copied or translated from these projects, and their libraries are not bundled.

- [Logitech's root feature specification](https://github.com/Logitech/cpg-docs/blob/f9107a4e3190d798f9be8c5d596f255f5e5741d2/hidpp20/features/0x0000-IRoot.rst), commit `f9107a4e3190d798f9be8c5d596f255f5e5741d2`: feature discovery, protocol-version ping and echoed marker.
- [Logitech HID++ 2.0 draft specification, 2012-06-04](https://drive.google.com/file/d/0BxbRzx7vEV7eU3VfMnRuRXktZ3M/view), pages 9–10: `1000` capability gating, normative category ranges/examples and zero-as-unknown sentinel. Download SHA-256 `c3d642248df91ee2d00420c359e4693ea36384b63d308abbc45bb4838919e58b`. The documented `30` category is Low; Linux's Windows-compatibility adjustment to Good is not adopted.
- [Solaar](https://github.com/pwr-Solaar/Solaar/tree/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver), GPL-2.0-or-later, commit `e7304c4c451cc9bb4f206a914844525e67856a28`: `base_usb.py` receiver IDs, `receiver.py` pairing fields, `base.py` framing/correlation and legacy ping errors, `hidpp10.py` register `0D`, `hidpp20.py` battery/name/identity fields.
- [Linux HID++ driver](https://github.com/torvalds/linux/blob/c03c1a14507dfaf8b856c2608f9ac216021fc222/drivers/hid/hid-logitech-hidpp.c), GPL-2.0-only: `1004` state-of-charge capability flag and category bitmask; `1000` mileage capability; legacy `07` category/charging values and status mapping.
- [libratbag](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c), MIT, commit `8235b5bb6032dea15901ab58a8218956f4494d08`: `1000` battery-level fields and charging status values.
- [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer/blob/f0e983e88900592237c7df9ae6b80b9dbeb3020b/battery.py), reviewed as a secondary reference. Its broader probing and numeric estimates were not adopted.

No runtime or physical hardware test has been performed. Those checks require the user's permission and Windows hardware access.
