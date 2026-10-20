# Alienware Pro headset: AWCC and USB protocol research

Static research on 20 October 2026 found a device-specific, packet-capture-derived
implementation: [romain-ncls/awpro](https://github.com/romain-ncls/awpro/tree/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88),
revision `d92f6df573b0a5b4c5551fcfc8d3311c435e7a88`. This is useful protocol
evidence; it is **not** independent hardware validation of this Windows application.

## Identification and transport

Sources: [`src/device.rs`](https://github.com/romain-ncls/awpro/blob/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88/src/device.rs),
[`src/protocol.rs`](https://github.com/romain-ncls/awpro/blob/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88/src/protocol.rs),
and [`packaging/70-awpro.rules`](https://github.com/romain-ncls/awpro/blob/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88/packaging/70-awpro.rules).

| Item | Upstream evidence |
| --- | --- |
| USB vendor | Dell `413c` |
| 2.4 GHz dongle product | `a529` |
| USB-C wired headset product | `a528`; exclude from the application's wireless-only monitoring |
| Vendor HID usage page | `ff13` |
| Request report | `06`, declared **Output**, 62 bytes including its report ID |
| Reply report | `07`, declared **Input**; upstream reads a 63-byte buffer including its report ID |
| Unsolicited notifications | Input report `08`; unnecessary for initial periodic reads |

Upstream explicitly distinguishes Linux's permissive feature-ioctl access from the
descriptor and Windows vendor software: AWCC uses `SET_REPORT` of type **Output** for
`06` and `GET_REPORT` of type **Input** for `07`. Using Windows feature-report APIs
for these reports is therefore an unverified transport assumption. Windows HID
capabilities must also be checked before sending or receiving buffers.

Only monitoring GET commands are needed. A GET request is zero-padded:

```text
06 C0 <opcode> 00 00 ...          (62 bytes)
```

Identity is the exception: its documented query includes parameters:

```text
06 C0 00 04 00 00 F5 28 00 ...   (62 bytes)
```

## Relevant replies

The byte offsets below include the report ID at offset zero.

| Command | Reply and interpretation |
| --- | --- |
| Wireless link `09` | `07 C0 09 01 00 <state>`; `CC` means up, `DD` means down, every other value is unknown. |
| Battery `0a` | `07 C0 0A 0A 00 00 <percent> <charging> ...`; percentage is byte 6, charging is byte 7. |
| Identity `00` with parameters | `07 C0 00 0C 00 <major> <minor> <patch> 00 28 A5 FF 01 ...`; little-endian headset product ID at bytes 9–10 is `a528`, including when reached through the `a529` dongle. |

Upstream golden battery examples, labelled as AWCC/on-hardware captures in its tests:

```text
07 C0 0A 0A 00 00 40 00 00 04 0F 40 00 02 03   (64%, on battery)
07 C0 0A 0A 00 00 64 01 00 36 10 64 00 02 2F   (100%, external power)
```

Only charging-byte values `00` and `01` are evidenced. Neither establishes a distinct
"fully charged" state: do not infer it from percentage alone. A genuine percentage
`00` is valid if the response is otherwise valid and the peripheral radio link is up.
Reject out-of-range percentages rather than clamping them into plausible values.

Upstream comments report that `09` still answers when the headset is off, while
battery and identity queries stop answering. Connecting the headset over USB-C drops
its radio link even though the dongle remains enumerated. Therefore USB presence,
successful USB writes, and a wired headset are insufficient to claim a connected
wireless peripheral. Checking the radio link before and after a battery query can
avoid displaying a reading after a detected disconnect during the refresh.

Upstream retries matching replies up to 20 times, 20 ms apart, and distinguishes
timeouts from explicit link-down. Preserve that distinction. Validate report ID,
direction, echoed command, minimum received length, relevant fixed fields, and
recognized status values; do not let a padded short response manufacture zeroes.
Unknown link bytes or exhausted I/O attempts must remain unknown/failed, with any
retained previous measurement explicitly stale.

The C# reader follows a narrower validation policy than upstream: it requires the
observed declared lengths `01`, `0c`, and `0a` for link, identity, and battery,
respectively, plus enough actual received bytes for those payloads. Upstream ignores
that length field. Changed firmware layouts therefore remain unavailable rather
than being decoded speculatively. Output is padded to the actual Windows descriptor
capacity; Input allocations use that descriptor rather than assuming upstream's
Linux buffer length. A failed native session is not reused for a diagnostic query;
the reader reports failure/unknown and allows the next refresh to reopen it.

## What was and was not obtained

The source was discovered through a permitted public GitHub repository search for
`alienware headset`, then cloned using the existing HTTPS Git configuration. No
awpro executable was run. The repository contains source and golden test bytes;
the raw `context/` packet captures mentioned by its comments are not present in
the inspected checkout. Hardware and capture assertions above are attributed to
that source, not to a local capture or test.

An official Dell product-support URL was attempted through the configured proxy:

```text
https://www.dell.com/support/home/en-us/product-support/product/alienware-pro-wireless-gaming-headset/drivers
```

It failed before HTTP with `CONNECT tunnel failed, response 403`. The current
restricted network policy did not include `www.dell.com`. No AWCC installer was
downloaded, extracted, executed, or decompiled. The attempted product URL's content
and validity have consequently not been confirmed. Inspecting the official support
page requires `www.dell.com`; official download links may additionally require
`dl.dell.com` or `downloads.dell.com`, depending on the links actually provided.
No alternate host, direct connection, or mirror was used to bypass this restriction.

Compilation, automated tests, application runtime verification, and physical hardware
verification remain separate outcomes. This research performed none of them.

## Licence

awpro is [MIT licensed](https://github.com/romain-ncls/awpro/blob/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88/LICENSE),
copyright 2026 Romain Nicolas. Preserve its copyright and permission notice if any
implementation is derived from or substantially incorporates that source. This
research document records protocol facts and source attribution, not vendor
endorsement or a claim that arbitrary Alienware headsets use the same protocol.
