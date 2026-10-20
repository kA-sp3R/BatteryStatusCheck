# Alienware Pro Wireless Gaming Headset

**Status: source-backed reader implemented; Windows application runtime and physical
hardware compatibility are unverified.** No local battery value or hardware result
has been observed. Other Alienware headset models remain unsupported.

The initial requested reference projects had no Pro-headset protocol. Follow-up
research found [romain-ncls/awpro](https://github.com/romain-ncls/awpro/tree/d92f6df573b0a5b4c5551fcfc8d3311c435e7a88),
whose source documents AWCC packet captures and the author's hardware observations.
The reader uses those protocol facts. See [research and provenance](../research/alienware-awcc.md)
for source revisions, captured response examples, transport limitations, and the
blocked attempt to obtain official Dell software.

## Supported identification and reads

- Only USB `413c:a529` (the **2.4 GHz dongle**) and vendor HID usage page `ff13`
  match the provider. Wired USB-C headset `413c:a528` is excluded.
- Descriptor capacities must hold the 62-byte request and at least a 17-byte
  identity reply. The Windows descriptor determines the Input buffer size; the
  upstream 63-byte Linux buffer is not treated as a Windows descriptor invariant.
  Output padding is handled by the native transport. Unsupported capacities cause
  an unavailable result before protocol I/O.
- Only GET-direction (`c0`) commands can be constructed: radio link `09`, headset
  identity `00` with documented parameters `00 f5 28 00`, and battery `0a`.
- Windows control transfers use **Output** report `06` and **Input** report `07`,
  matching the descriptor types described by the upstream author. Feature reports
  and guessed firmware/settings opcodes are not used.
- The radio link must report `cc` before the identity/battery reads and after the
  battery read. The headset's identity must report model `a528`. A `dd` link is
  disconnected even while its dongle remains enumerated; other link bytes are
  unknown and cannot produce a current battery reading.
- Battery byte 6 is a percentage; genuine `0` is preserved and values above `100`
  fail validation. Charging byte 7 maps `0` to discharging and `1` to charging;
  other values remain unknown. A 100% value does not by itself establish a distinct
  fully-charged status.

Replies require report `07`, direction `c0`, the matching opcode, observed zero
header byte 4, and a complete payload. The implemented known payload lengths are
`01` for link, `0c` for identity and `0a` for battery; battery payload byte 5 must
match the observed zero prefix. Upstream does not enforce declared payload lengths.
This application deliberately accepts only the captured layouts it decodes, instead
of allowing padded/truncated or changed layouts to manufacture plausible values.
New firmware with different layouts may therefore report failed/unavailable until
its response is understood. No undocumented checksum is inferred.

Queries poll for their matching reply up to 20 times, 20 ms apart. Any query failure
remains failed with unknown link state; timeouts do not establish disconnection.
Native timeouts can close the session, so it is not reused for further diagnosis.
A later refresh opens a new session. The monitor retains any previous measurement
only as stale, with its original sample time.

## Limits

Association is to the physical receiver plus a reported Pro-headset model. The
documented identity reply does not supply a unique headset serial number, so this
does not establish identity continuity if headsets of the same model are exchanged
on a receiver. No claim is made that the firmware never caches a battery value:
the protocol has no documented battery sample timestamp or transaction nonce.

No application, installer, third-party CLI, or hardware command was executed during
this research/implementation. Compilation and automated-test outcomes are tracked
separately from runtime and hardware verification. Runtime/hardware work requires
the user's permission and a Windows machine with the headset.

awpro is MIT licensed, copyright 2026 Romain Nicolas. Its notice is retained in
the repository's third-party notices. No source from the previously inspected GPL
or unlicensed projects was copied.
