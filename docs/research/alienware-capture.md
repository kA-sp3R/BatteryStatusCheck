# Alienware Pro: offline capture analysis

The known wireless dongle is `413C:A529`; `413C:A528` is the wired headset.
Do not identify a headset by Dell's vendor ID alone. The upstream
[awpro protocol notes](https://github.com/romain-ncls/awpro/blob/d92f6df5/src/protocol.rs)
describe vendor usage page `FF13`, Output report `06` (62 bytes), Input reply
`07` (read into an upstream 63-byte Linux buffer), and notification `08`. Actual
Windows report lengths must come from the descriptor. These are protocol research findings,
not evidence that this application's Windows transport works on hardware.

`tools/alienware/analyze_capture.py` is an **offline** helper. It reads exported
JSON, or invokes an installed `tshark` with `-r` to read an existing capture.
It never opens HID, launches AWCC, captures live traffic, changes a driver,
or sends a manufacturer command. Python 3.11+ is sufficient for JSON input;
pcap/pcapng input additionally needs Wireshark's `tshark`.

## Future, explicitly authorized Windows capture

Application runtime and physical hardware verification require separate user
permission before performing these steps. No such capture was performed here.
Use an existing, trusted Wireshark/USBPcap installation. Installing USBPcap or
restarting devices is outside this procedure; do not enable Windows test signing.

1. Record the headset model, dongle VID/PID, firmware, Windows version, AWCC
   version, and whether another management app is running. Keep this app closed.
2. In Wireshark's USBPcap interface settings, identify the root hub and exact
   dongle USB address. The address is temporary and can change after replugging.
   Root-hub name, USB bus number, interface number, and USB address are different
   identifiers; record each from actual evidence.
3. Restrict capture to that dongle address. USBPcap's documented manual command
   is below; substitute the observed root hub/address and a new output path.
   Run only after capture permission, from an elevated terminal if required by
   the existing installation. Stop using Ctrl+C. Leave playback/microphone idle;
   retain only control/interrupt traffic when sharing an export.

   ```powershell
   $RootHub = '\\.\USBPcap1' # substitute the observed root hub
   $UsbAddress = Read-Host 'Observed dongle USB address'
   & 'C:\Program Files\USBPcap\USBPcapCMD.exe' -d $RootHub --devices $UsbAddress --inject-descriptors -o '.\alienware-session.pcap'
   ```

4. Keep the dongle plugged in and capture these separate, timestamped scenarios:
   headset off/AWCC closed; headset off/AWCC open; headset on and associated;
   charging cable attached; charging cable detached; headset off again. Allow
   AWCC to refresh between transitions. A plugged dongle or USB charging cable
   alone does not prove that the radio peripheral is connected. Replugging the
   receiver requires a new address check, filter, and capture file.
5. For each interval, record UTC start/end, physical headset state, charging
   cable state, AWCC connection indication, displayed percentage, and screenshot
   or observation reference. Record a genuine displayed `0` as `0`. Use `null`
   for unavailable percentages; label cached/unchanged AWCC values `stale` or
   `unknown` unless freshness is established. Obtain independent sessions at
   naturally different measured battery levels before inferring any byte mapping.

For timestamps in Windows PowerShell:

```powershell
(Get-Date).ToUniversalTime().ToString('o')
```

The optional markers file is a JSON array with `scenario`, `start`, `end`,
`awcc_percent` (observed integer 0–100 or null), `awcc_reading_state` (`current`,
`stale`, `unavailable`, `unknown`), and free-form evidence fields such as
`headset_state`, `charging_cable`, `awcc_connection`, `evidence`, and `notes`.
Times must have an explicit UTC offset. Intervals are start-inclusive/end-exclusive.
Omit markers until actual observations exist; the helper supplies no sample values.

## Analyze existing files

Substitute the observed bus and address; the numbers below are parameters,
not assumed hardware identifiers:

```powershell
$UsbBus = Read-Host 'Observed USB bus number'
$UsbAddress = Read-Host 'Observed dongle USB address'
python tools/alienware/analyze_capture.py .\alienware-session.pcap --bus $UsbBus --device $UsbAddress --output .\analysis.json
```

Alternatively export USBPcap JSON with raw bytes, then analyze without `tshark`:

```powershell
$Filter = "usb.bus_id == $UsbBus && usb.device_address == $UsbAddress && (usb.transfer_type == 1 || usb.transfer_type == 2)"
& 'C:\Program Files\Wireshark\tshark.exe' -r '.\alienware-session.pcap' -Y $Filter -T json -x | Out-File -Encoding utf8 '.\capture.json'
python tools/alienware/analyze_capture.py .\capture.json --bus $UsbBus --device $UsbAddress --markers .\markers.json --output .\analysis-with-evidence.json
```

After independently confirming dongle `413C:A529` and vendor collection `FF13`,
add `--decode-awpro` to annotate known monitoring command/reply fields (`00`
identity, `09` radio link, `0A` battery). This opt-in interpretation checks report
direction, IDs, lengths, headers, and successful USB completion. A decoded battery
field of `0` stays `0`; unknown/out-of-range data is not interpreted. All such
fields remain historical observations, and a battery response alone does not
establish an associated, currently connected peripheral.

Preserve the original capture and SHA-256. Analysis outputs refuse to overwrite
existing files. Truncated packets, missing raw bytes, and no matching traffic
are reported as errors. The helper analyzes USBPcap control/interrupt frames;
it does not analyze bulk/audio traffic or other capture link formats.

## Interpret the report

- `transfers` groups USB submission/completion frames by bus/address/IRP, with
  new generations when IRP IDs are reused. Missing submissions, incomplete
  transfers, and failed USB statuses remain visible.
- Each payload records actual bytes/length, direction, frame, timestamp, and
  control setup metadata. Identical submit/completion payloads in one IRP are
  flagged; only the expected data phase contributes to `payload_patterns`.
  Identical bytes in separate polls remain separate observations.
- A HID class GET/SET_REPORT setup's `wValue` proves report type/ID; an interrupt
  payload's first byte alone does not. Numbered USB reports contain the report
  ID; unnumbered USB reports do not include Windows' synthetic leading zero.
- IRP correlation proves a USB request/completion pair. Pairing a SET_REPORT
  command with a later GET_REPORT or interrupt message requires protocol-specific
  command/function/sequence evidence. Temporal proximity is insufficient.
- Compare the same report/function/length across off/on and charging transitions,
  then across independent battery levels. Validate opcode/length, connection
  status, and response association before interpreting a candidate byte. Do not
  turn constant, invalid, stale, disconnected, or failed data into a percentage.

The helper deliberately infers **no live readings**. Optional AW Pro field
interpretation uses the cited public protocol; it does not promote old capture
bytes into current battery/connection state or guess undocumented opcodes.
Synthetic parser tests are separate from Windows compilation, application runtime
checks, real capture analysis, and physical-device verification.

Sources consulted: [USBPcap CMD options](https://github.com/desowin/usbpcap/blob/477b6edcbd7e99a47f77afc0c4168a9ebee603bb/USBPcapCMD/cmd.c#L1327),
[Wireshark USBPcap header/stage decoder](https://github.com/wireshark/wireshark/blob/master/epan/dissectors/packet-usb.c),
[Wireshark tshark manual](https://www.wireshark.org/docs/man-pages/tshark.html).
The helper independently implements documented packet-format facts; it does not
copy third-party driver, HID, or application code.
