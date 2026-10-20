# Alienware Pro Wireless Gaming Headset

**Status: unsupported.** No battery reader is implemented. The application's capability
record is `AlienwareSupport`; it does not claim that an enumerated Dell/Alienware device
is this headset and does not send speculative HID commands.

The requested reference projects do not contain a Pro headset battery protocol at the
revisions inspected:

| Reference | Revision | Result |
| --- | --- | --- |
| [FerrisPulse](https://github.com/Ferris-echo/FerrisPulse/tree/ffa66068cc5adc149770b58073ab96828ed92092) | `ffa66068` | No Alienware implementation found. |
| [2.4G-battery-viewer](https://github.com/yszyszysz233/2.4G-battery-viewer/tree/f0e983e88900592237c7df9ae6b80b9dbeb3020b) | `f0e983e8` | Its headset reader targets DARKRIM, not Alienware. |
| [HeadsetControl](https://github.com/Sapd/HeadsetControl/tree/b816f5d54cf42fd5dd2398a29f1a8239b9b880bf) | `b816f5d5` | No Alienware implementation found. |
| [EZ2.4G](https://github.com/Ap0LoG1zE/EZ2.4G/tree/025f829dce0e023e2f9ade8629ad4f880431de94) | `025f829d` | No Alienware implementation found. |
| [alienfx-tools](https://github.com/T-Troll/alienfx-tools/tree/8171ac38b97400fd433c09090638af6d87c6fdcb) | `8171ac38` | No Pro headset implementation found. |

The [USB ID repository](https://github.com/usbids/usbids/blob/5eb613762db1e34b9218da63a88f22dd26f743c5/usb.ids)
lists vendor `187c` as Alienware Corporation, with lighting/gamepad products, and
`0b0e:a50a` as the older AW988 headset. Neither is evidence of the **Pro** headset's
dongle identity. Matching every Dell or Alienware VID would misidentify unrelated
hardware; reusing an AW988 protocol would also be unsupported.

Implementing this device requires a trustworthy Pro-specific protocol reference or,
with the user's permission, investigation of its descriptors and relevant vendor
software's battery traffic. Required facts include dongle VID/PID, HID collection and
report lengths, request/response validation, actual percentage encoding, charging
semantics, and an explicit peripheral-presence signal. A plugged-in dongle alone must
never establish headset connection. Missing data must never become `0%`.

No application runtime or physical hardware verification was performed. No third-party
source code was copied: FerrisPulse is GPL-3.0; no licence file was found for
2.4G-battery-viewer at the inspected revision. These references document research
scope, not a claim of compatibility or a basis for copying implementation code.
