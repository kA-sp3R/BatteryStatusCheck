# Logitech receiver battery support

Source-backed software implementation for v1.0.0. Runtime and hardware verification are recorded as [deferred work](../deferred-verification.md); software test/build outcomes are recorded separately in [verification status](../verification.md).

## Selected transport and association

Only Logitech USB VID `046D`, HID++ vendor usage page `FF00`, usage `0001`/`0002`, and these **23 explicit receiver IDs** are selected:

| Receiver family | Product IDs |
| --- | --- |
| Unifying | `C52B`, `C532` |
| LIGHTSPEED | `C539`, `C53A`, `C53D`, `C53F`, `C541`, `C545`, `C547`, `C54D` |
| Bolt | `C548` |
| Nano | `C518`, `C51A`, `C51B`, `C521`, `C525`, `C526`, `C52E`, `C52F`, `C531`, `C534`, `C535`, `C537` |

Bluetooth and directly attached USB mice are excluded. Receiver admission requires a documented ID and the HID++ vendor collection. Battery functionality then depends on the actual peripheral feature set and replies. Product-family names alone do not establish compatibility; physical G-series/productivity mouse and firmware combinations remain in the deferred record.

For non-Bolt receivers, receiver-information register `0x2B5` (read command `83 B5`) subregister `03` supplies the slot limit when supported. Pairing subregisters `20 + slot - 1` and extended pairing `30 + slot - 1` supply wireless IDs and serials. Catalog defaults are six Unifying slots, one LIGHTSPEED slot and one Nano slot, except `C534` with two; accepted receiver metadata can supply a bounded slot count.

Bolt uses six slots and its own pairing subregisters **`51`–`56`** (`50 + slot`). Its pairing payload contains the device kind at byte 1, a little-endian wireless ID at bytes 2–3, and serial bytes 4–7. Classic pairing/extended-serial offsets are not substituted for that layout. Unifying/Bolt empty slots must be established through their pairing replies.

Nano receivers without usable pairing metadata require HID++ 2 unit identity from feature `0003` before accepting a battery. The same unit preserves identity across temporary metadata loss; a changed unit/confirmed pairing retires the previous device rather than transferring its measurement. Nano HID++ 1 paths without slot identity stay unsupported: global receiver battery registers are not assigned to a guessed peripheral.

All peripheral commands target their receiver slot (`1`–`6`), never the receiver address `FF`. A fresh root-feature ping, with a random echoed marker, is required before reading battery fields. Pairing information and an enumerated receiver alone never establish connection. Identities include receiver identity, slot and device-reported WPID/serial or unit ID when available. Battery monitoring sends no pairing, notification-configuration, profile or polling-rate changes. Complete association evidence can retire a former peer; incomplete discovery preserves uncertainty and prior measurements only as stale.

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

Feature indices are discovered through `0000` GetFeature. Visible, non-obsolete feature versions use their baseline functions according to Logitech's backwards-compatibility rule; hidden/obsolete features are excluded and replies remain validated. Optional `0003` and `0005` features supply unit ID and peripheral name; no model name is inferred from a receiver name. Replies must match report format, slot, feature/register, function and HID++ software ID. Pairing queries additionally match their echoed subregister. Ping also matches its random marker. Unrelated reports are ignored with a 700 ms / 64-packet bound. HID++ 1.0 register transactions lack software IDs in the protocol and cannot provide the same correlation strength as feature calls; simultaneous vendor software may cause failures.

Explicit legacy connection-request-failed / unknown-device errors yield disconnected. Resource errors, I/O errors, malformed replies and timeouts yield failed/unknown connection, not a fabricated disconnected state. Prior successfully sampled values may be retained by the monitor only as stale, with their original timestamp.

## Research sources and licences

This C# implementation was written independently from the protocol facts below. No source code was copied or translated from these projects, and their libraries are not bundled.

- [Logitech's root feature specification](https://github.com/Logitech/cpg-docs/blob/f9107a4e3190d798f9be8c5d596f255f5e5741d2/hidpp20/features/0x0000-IRoot.rst), commit `f9107a4e3190d798f9be8c5d596f255f5e5741d2`: feature discovery, protocol-version ping and echoed marker.
- [Logitech HID++ 2.0 draft specification, 2012-06-04](https://drive.google.com/file/d/0BxbRzx7vEV7eU3VfMnRuRXktZ3M/view), pages 9–10: `1000` capability gating, normative category ranges/examples and zero-as-unknown sentinel. Download SHA-256 `c3d642248df91ee2d00420c359e4693ea36384b63d308abbc45bb4838919e58b`. The documented `30` category is Low; Linux's Windows-compatibility adjustment to Good is not adopted.
- [Solaar](https://github.com/pwr-Solaar/Solaar/tree/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver), GPL-2.0-or-later, commit `e7304c4c451cc9bb4f206a914844525e67856a28`: `base_usb.py` Unifying/LIGHTSPEED/Bolt/Nano receiver IDs and capacities, `receiver.py` classic and Bolt pairing layouts, `base.py` framing/correlation and legacy ping errors, `hidpp10.py` register `0D`, `hidpp20.py` battery/name/identity fields.
- [Linux HID++ driver](https://github.com/torvalds/linux/blob/c03c1a14507dfaf8b856c2608f9ac216021fc222/drivers/hid/hid-logitech-hidpp.c), GPL-2.0-only: `1004` state-of-charge capability flag and category bitmask; `1000` mileage capability; legacy `07` category/charging values and status mapping.
- [libratbag](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c), MIT, commit `8235b5bb6032dea15901ab58a8218956f4494d08`: `1000` battery-level fields and charging status values.
- [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer/blob/f0e983e88900592237c7df9ae6b80b9dbeb3020b/battery.py), reviewed as a secondary reference. Its broader probing and numeric estimates were not adopted.

Runtime/hardware work is deferred in the saved record. The implemented protocol paths and scripted regressions do not certify a physical device.
