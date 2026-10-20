# Logitech manual polling-rate control

Implemented from public protocol descriptions; **physical receiver, mouse and firmware compatibility is unverified**. No application or hardware tests were performed during implementation. Windows compilation and synthetic unit tests are separate evidence.

## Scope and capability provenance

The controller uses the same explicit Logitech USB 2.4 GHz receiver IDs and HID++ vendor collection as battery discovery. Bluetooth, wired mouse IDs, unidentified peripherals, unknown receivers and receiver-only placeholders are rejected. Battery monitoring never calls this controller.

Only report-rate feature **version 0** is implemented. Other advertised versions are refused without falling back to a different protocol. Options come from a fresh feature request addressed to the selected mouse's receiver slot, not from its product name or a presumed receiver maximum:

| Feature | Capability request | Current request | One setting request | Encoding |
| --- | --- | --- | --- | --- |
| `8060` | Function `00` | `10` | `20` | Capability byte bit N means N+1 milliseconds; current/set byte is milliseconds. |
| `8061` | Function `10` | `20` | `30` | Big-endian 16-bit capability mask. Bits/indexes 0–6 mean 125, 250, 500, 1000, 2000, 4000, 8000 Hz. |

`8061` is preferred when advertised. Unknown mask bits are ignored. `8060` periods whose frequency is not an integer number of Hz (3, 6, 7 ms) are not rounded into invented rates; a current value with such an encoding is unavailable in this integer-Hz interface. Five milliseconds is genuinely 200 Hz when advertised.

The `8061` mask model follows Solaar's device-specific capability query. Its source documents PRO X 2 `40A9` through receiver `C54D`, and PRO X 2 Superstrike `40BD`, advertising `8061 V0`. This establishes the protocol model, not compatibility of every listed USB receiver or firmware with 8000 Hz. No source was found establishing that the mask is a negotiated receiver ceiling; the UI reports it as **mouse-advertised options through this receiver**. A high rate is offered only when its bit is actually returned on that association. No blanket 8 kHz model claim, guessed product maximum, or arbitrary software ceiling is substituted.

Read-back verifies a device-reported configuration. It does **not** measure the effective USB interrupt frequency, radio timing, receiver throughput, or physical performance. Manufacturer software and firmware may impose additional limits or override a setting.

## Association and deliberate changes

- Accept only the battery provider's exact identities: `receiver/logitech/slot/WPID/serial` or `receiver/logitech/slot/unit/unitId`. Require matching vendor, receiver, canonical slot 1–6 and non-placeholder identifiers.
- Ping the exact slot using a fresh random echoed marker. Verify the full pairing WPID and serial, or freshly query the unit ID through feature `0003`. A changed identity yields disconnected for the selected peripheral, even if a replacement is present.
- Require feature `0005` GetDeviceType to report mouse (`03`). For pairing identities, also require the receiver's HID++ 1 kind to be mouse (`02`). Keyboards and unknown types are refused.
- If `8100` onboard profiles exists, require its current mode to be host (`02`). Onboard/unknown modes are refused. The controller never switches profile mode or edits profile memory.
- Before a setting, repeat identity/type, profile-mode and capability queries. Check cancellation and the caller's authorization immediately before sending the single setting command. A revoked or superseded Apply request sends no setting.
- Match reply slot, feature/function and software ID; pairing queries also match the echoed subregister and ping matches its marker. Reads are bounded by 700 ms and 64 unrelated reports. HID++ 1 receiver register responses have no software transaction ID; their correlation is limited to header, subregister and expected full identity.
- After the setting acknowledgement, revalidate the same mouse and profile mode, then query the current rate. A mismatch, changed identity, error, cancellation or timeout leaves the request unverified. No retry or rollback is sent. A timed-out transport is never reused; the write may already have reached the mouse.

## Research and licences

The C# controller and codecs were independently written from protocol facts. No source code was copied or translated, and no external library is bundled.

- [Solaar `settings_templates.py`](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver/settings_templates.py), GPL-2.0-or-later: `ReportRate`, `ExtendedReportRate`, capability byte order, function IDs and encoding table.
- [Solaar capability notes](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/docs/capabilities.md): extended report-rate table and onboard-profile limitations. The same revision's `docs/devices/PRO X 2 40A9.text` and `PRO X 2 Superstrike 40BD.txt` document version 0 and association examples.
- [libratbag `hidpp20.c`](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c) and `hidpp20.h`, MIT: `8060` capability, current/set fields and `8100` host/onboard mode query.
- [Logitech root feature specification](https://github.com/Logitech/cpg-docs/blob/f9107a4e3190d798f9be8c5d596f255f5e5741d2/hidpp20/features/0x0000-IRoot.rst): feature discovery and protocol-version ping correlation.

`LogitechPollingTests` uses an in-memory scripted transport to cover exact masks, identity replacement, mouse-only controls, profile/version refusal, authorization revocation, changing capabilities, one-write semantics, timeout/cancellation and read-back failures. Passing these tests establishes those software rules; it does not establish hardware compatibility.
