# Logitech manual polling-rate control

Source-backed software implementation for v1.0.0. Physical receiver/mouse compatibility, effective USB timing and firmware persistence are recorded as [deferred work](../deferred-verification.md). Build and automated-test outcomes remain separate in [verification status](../verification.md).

## Receiver scope and feature versions

The controller uses the same [23 explicit Logitech receiver IDs](logitech.md) and HID++ vendor collection as battery discovery, including Unifying, LIGHTSPEED, Bolt and Nano. The selected mouse must have a canonical slot/pairing or unit identity. Bluetooth, wired IDs, unknown receivers and unidentified placeholders are excluded. Battery monitoring never calls this controller.

Logitech's IRoot specification requires later versions of a feature to retain backwards-compatible functionality. The controller therefore accepts visible, non-obsolete `8060`/`8061` feature versions and invokes only their original functions. It validates advertised capabilities and every decoded reply; a later version does not authorize guessed commands or a presumed maximum rate. Hidden or obsolete profile-mode features cannot establish permission to write.

| Feature | Capability request | Current request | Rate setting | Encoding |
| --- | --- | --- | --- | --- |
| `8060` | Function `00` | `10` | `20` | Capability bit N means N+1 milliseconds; current/set byte is milliseconds. |
| `8061` | Function `10` | `20` | `30` | Big-endian 16-bit mask; bits/indexes 0–6 mean 125, 250, 500, 1000, 2000, 4000, 8000 Hz. |

`8061` is preferred when advertised. Unknown mask bits are ignored. `8060` periods with non-integer Hz values (3, 6, 7 ms) are not rounded; an active value with such an encoding is unavailable in the integer-Hz interface. Five milliseconds represents 200 Hz when advertised. Options come from the selected mouse through its current receiver association, rather than a product-name maximum or a negotiated receiver ceiling.

The source examples PRO X 2 `40A9` through receiver `C54D` and PRO X 2 Superstrike `40BD` advertise `8061 V0`. The implementation also accepts the baseline functions of later feature versions under the official compatibility rule. These protocol facts do not establish physical 8000 Hz compatibility for every receiver or firmware.

## Identity checks

- Accept the battery provider's exact identities: `receiver/logitech/slot/WPID/serial` or `receiver/logitech/slot/unit/unitId`, with matching receiver, canonical slot 1–6 and non-placeholder identifiers.
- Ping the exact slot with a fresh random echoed marker. Verify its current wireless ID and serial, or re-query unit ID through feature `0003`. Identity replacement invalidates the selected target.
- Bolt pairing uses `51`–`56`, little-endian wireless IDs and the serial embedded in that reply. Classic receivers use their distinct pairing and extended-serial offsets. The layouts are not interchanged.
- Require feature `0005` GetDeviceType to identify a mouse (`03`). Pairing-based identities additionally require the receiver-reported device kind to be mouse (`02`). Keyboards and unknown kinds cannot receive rate changes.
- Correlate reply slot, feature/function and software ID; pairing queries also match their echoed subregister and pings their random marker. Reads are bounded by 700 ms and 64 unrelated reports. HID++ 1 register transactions lack software IDs and have weaker correlation than feature calls.

## Separate software-mode opt-in

If `8100` reports onboard mode (`01`), Detect returns **RequiresHostMode**, the capability options and no claimed active software-mode rate. The popup presents a separate **Switch Logitech to software mode on Apply** checkbox. The user must check it, select a rate and click Apply. Detect, battery refresh and selecting a rate send no configuration writes.

Switching to software mode can change DPI and button behavior. The controller writes only `8100` SetOnboardMode function `10` with mode `02`, after repeating identity/capability checks and checking both the ordinary Apply guard and the separate mode-change guard. It does not edit profile memory, pair devices or change firmware. An unknown mode or hidden/obsolete profile feature is refused.

After the requested mode change, readback must confirm software mode. The controller re-probes the same mouse, discovers the effective rate feature, rechecks capabilities and re-encodes the requested rate before any rate setting. If the new software configuration already reports the requested rate, no additional rate write is necessary. A failed or canceled operation can leave software mode active; no automatic rollback or later startup restore is sent. The extra choice resets with detection, selection or control/session reset.

## Deliberate rate setting and readback

Before a rate write, repeat identity/type, profile-mode and capability checks. Cancellation and the active user's guards are checked immediately before each configuration write. A host-mode switch and a rate write are separate commands, each sent at most once per Apply. No automatic retries are made.

After a rate acknowledgement, revalidate the same mouse and software mode, then read the reported rate. Verification requires Supported state, an advertised option and a matching rate; RequiresHostMode can never be a verified result. A mismatch, changed identity, cancellation or timeout leaves the outcome unverified. A timeout stops transport reuse, and a partial sequence may already have changed mode or rate. Detect again before another deliberate attempt.

Readback confirms reported configuration. Actual USB interrupt frequency, radio timing, throughput, persistence and vendor-software coexistence are deferred checks.

## Research and licences

The controller/codecs independently implement protocol facts. No upstream source code or external library is bundled.

- [Logitech IRoot specification](https://github.com/Logitech/cpg-docs/blob/f9107a4e3190d798f9be8c5d596f255f5e5741d2/hidpp20/features/0x0000-IRoot.rst), commit `f9107a4e3190d798f9be8c5d596f255f5e5741d2`: feature discovery, hidden/obsolete flags, protocol ping and the requirement that new feature versions remain backwards compatible.
- [Solaar settings templates](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/lib/logitech_receiver/settings_templates.py), GPL-2.0-or-later: report-rate capability queries, function IDs, masks and encodings. Its `base_usb.py`/`receiver.py` describe receiver admission and Bolt pairing.
- [Solaar capability notes](https://github.com/pwr-Solaar/Solaar/blob/e7304c4c451cc9bb4f206a914844525e67856a28/docs/capabilities.md), same revision: extended report rates and profile-mode limitations; device records document the `40A9`/`40BD` examples.
- [libratbag HID++ 2 implementation](https://github.com/libratbag/libratbag/blob/8235b5bb6032dea15901ab58a8218956f4494d08/src/hidpp20.c), MIT: `8060` and `8100` host/onboard mode query/set functions.

Scripted controller/service regressions cover association, Bolt layout, later feature versions, read-only detection, separate mode opt-in, revoked guards, partial changes, changed capabilities and readback. These software tests do not exercise physical devices.
