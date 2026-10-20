#!/usr/bin/env python3
"""Offline USBPcap analysis; never opens a device, starts AWCC, or initiates capture."""
from __future__ import annotations

import argparse
from collections import Counter
from datetime import datetime
import hashlib
import json
from pathlib import Path
import struct
import subprocess
import sys


def one(value):
    return value[0] if isinstance(value, list) and value else value


def field(tree, name):
    if isinstance(tree, dict):
        if name in tree:
            return one(tree[name])
        for child in tree.values():
            result = field(child, name)
            if result is not None:
                return result
    return None


def epoch(value):
    if value is None:
        return None
    try:
        return float(value)
    except (TypeError, ValueError):
        instant = datetime.fromisoformat(str(value).replace("Z", "+00:00"))
        if instant.tzinfo is None:
            raise ValueError("Scenario times require a UTC offset.")
        return instant.timestamp()


def decode_packet(item, fallback_number):
    """Decode documented USBPcap pseudoheader, independent of HID dissector output."""
    if "usbpcap_raw_hex" in item:
        raw_hex = item["usbpcap_raw_hex"]
        number = int(item.get("frame", fallback_number))
        timestamp = epoch(item.get("time_epoch"))
    else:
        layers = item.get("_source", {}).get("layers", {})
        raw_hex = one(layers.get("frame_raw"))
        if raw_hex is None or field(layers, "usb.usbpcap_header_len") is None:
            raise ValueError("Expected USBPcap tshark JSON exported with -T json -x.")
        number = int(field(layers, "frame.number") or fallback_number)
        timestamp = epoch(field(layers, "frame.time_epoch"))
    raw = bytes.fromhex(raw_hex.replace(":", ""))
    if len(raw) < 27:
        raise ValueError(f"Frame {number}: truncated USBPcap header.")
    header_length, irp, status, function, info, bus, device, endpoint, transfer, length = struct.unpack_from(
        "<HQIHBHHBBI", raw)
    if header_length < 27 or header_length > len(raw):
        raise ValueError(f"Frame {number}: invalid USBPcap header length.")
    payload = raw[header_length:]
    if len(payload) != length:
        raise ValueError(f"Frame {number}: captured payload length differs from USBPcap dataLength; export a full capture.")
    stage = raw[27] if transfer == 2 and header_length >= 28 else None
    setup = None
    if transfer == 2 and stage == 0:
        if len(payload) < 8:
            raise ValueError(f"Frame {number}: truncated USB setup packet.")
        bm, request, value, index, requested_length = struct.unpack_from("<BBHHH", payload)
        setup = {"bm_request_type": bm, "request": request, "value": value,
                 "interface_or_index": index, "requested_length": requested_length}
        if bm & 0x60 == 0x20 and request in (1, 9):
            setup.update(hid_report_type=value >> 8, hid_report_id=value & 0xFF)
        payload = payload[8:]
    return {"frame": number, "time_epoch": timestamp, "irp": f"0x{irp:016x}",
            "bus": bus, "device": device, "endpoint": endpoint, "transfer_type": transfer,
            "phase": "completion" if info & 1 else "submit", "status": status,
            "function": function, "control_stage": stage, "setup": setup,
            "payload_hex": payload.hex(), "payload_length": len(payload)}


def load_markers(path):
    if path is None:
        return []
    data = json.loads(path.read_text(encoding="utf-8-sig"))
    if not isinstance(data, list):
        raise ValueError("Markers must be a JSON array.")
    for marker in data:
        marker["start_epoch"] = epoch(marker.pop("start"))
        marker["end_epoch"] = epoch(marker.pop("end"))
        if marker["start_epoch"] is None or marker["end_epoch"] is None:
            raise ValueError("Scenario start/end times are required.")
        if marker["end_epoch"] <= marker["start_epoch"]:
            raise ValueError("Scenario end must follow its start.")
        percent = marker.get("awcc_percent")
        if percent is not None and (type(percent) is not int or not 0 <= percent <= 100):
            raise ValueError("AWCC percentage must be an observed integer 0..100 or null.")
        if marker.get("awcc_reading_state", "unknown") not in ("current", "stale", "unavailable", "unknown"):
            raise ValueError("Invalid AWCC reading state.")
        if not marker.get("scenario"):
            raise ValueError("Each marker requires a scenario name.")
    return data


def awpro_fields(payload):
    """Interpret only documented monitoring frames; these are historical fields, not live readings."""
    data = bytes.fromhex(payload["hex"])
    if len(data) < 5 or data[1] != 0xC0 or data[4] != 0 or data[2] not in (0x00, 0x09, 0x0A):
        return None
    command = data[2]
    result = {"source": "romain-ncls/awpro d92f6df5", "command": command,
              "freshness": "historical capture; never a current reading"}
    if data[0] == 6 and len(data) == 62 and payload["direction"] == "out":
        expected = bytes.fromhex("00f52800") if command == 0 else b""
        if data[3] != len(expected) or data[5:5 + len(expected)] != expected:
            return None
        return result | {"kind": "monitoring_get_command"}
    if data[0] != 7 or len(data) != 63 or payload["direction"] != "in":
        return None
    if data[3] != {0: 12, 9: 1, 10: 10}[command]:
        return None
    result["kind"] = "monitoring_reply"
    if command == 0:
        result["headset_product_id_field"] = f"0x{int.from_bytes(data[9:11], 'little'):04x}"
        result["firmware_fields"] = list(data[5:8])
    elif command == 9:
        result["wireless_link_field"] = {0xCC: "connected", 0xDD: "disconnected"}.get(data[5], "unknown")
        result["wireless_link_raw"] = data[5]
    else:
        if data[5] != 0 or data[6] > 100:
            return None
        result["battery_percentage_field"] = data[6] # Zero is a valid observed protocol field.
        result["charging_field"] = {0: "discharging", 1: "charging"}.get(data[7], "unknown")
        result["charging_raw"] = data[7]
        result["connection_requirement"] = "Requires a separately associated radio-link response; dongle presence is insufficient."
    return result


def analyze(items, bus, device, markers=(), decode_awpro=False):
    transfers = []
    pending = {}
    selected_frames = 0
    previous_frame = 0
    for number, item in enumerate(items, 1):
        packet = decode_packet(item, number)
        if packet["frame"] <= previous_frame:
            raise ValueError("Packets must have unique, increasing capture frame numbers.")
        previous_frame = packet["frame"]
        if packet["bus"] != bus or packet["device"] != device or packet["transfer_type"] not in (1, 2):
            continue
        selected_frames += 1
        key = (bus, device, packet["irp"])
        existing = pending.get(key)
        continuation = packet["transfer_type"] == 2 and packet["control_stage"] in (1, 2, 3)
        if existing is None or (packet["phase"] == "submit" and not continuation):
            if existing is not None:
                existing["warnings"].append("IRP identifier reused before a terminal completion; prior transfer remains incomplete.")
            existing = {"id": len(transfers) + 1, "irp": packet["irp"], "bus": bus, "device": device,
                        "endpoint": packet["endpoint"], "transfer_type": packet["transfer_type"],
                        "frames": [], "setup": None, "payloads": [], "complete": False,
                        "warnings": [], "scenarios": []}
            if packet["phase"] == "completion":
                existing["warnings"].append("Submission missing: capture begins mid-transfer or packets were lost.")
            transfers.append(existing)
            pending[key] = existing
        existing["frames"].append(packet["frame"])
        if packet["setup"] is not None:
            existing["setup"] = packet["setup"]
        if packet["phase"] == "completion":
            existing["completion_status"] = f"0x{packet['status']:08x}"
            if packet["status"] != 0:
                existing["warnings"].append(f"Completion frame {packet['frame']} has nonzero USBD status.")
        for marker in markers:
            timestamp = packet["time_epoch"]
            if timestamp is not None and marker["start_epoch"] <= timestamp < marker["end_epoch"]:
                if marker["scenario"] not in existing["scenarios"]:
                    existing["scenarios"].append(marker["scenario"])
        if packet["payload_length"]:
            setup = existing["setup"]
            inbound = bool(setup["bm_request_type"] & 0x80) if setup else bool(packet["endpoint"] & 0x80)
            expected_phase = "completion" if inbound else "submit"
            payload = {"frame": packet["frame"], "phase": packet["phase"],
                       "direction": "in" if inbound else "out", "length": packet["payload_length"],
                       "hex": packet["payload_hex"], "time_epoch": packet["time_epoch"],
                       "canonical_data_phase": packet["phase"] == expected_phase,
                       "report_id": setup.get("hid_report_id") if setup else None,
                       "report_id_source": "HID class setup wValue" if setup and "hid_report_id" in setup else "unknown; requires report descriptor",
                       "first_payload_byte": int(packet["payload_hex"][:2], 16)}
            # Identical submit/completion payloads belong to one URB, not two battery samples.
            duplicates = [p["frame"] for p in existing["payloads"] if p["hex"] == payload["hex"]]
            if duplicates:
                payload["same_transfer_duplicate_of_frames"] = duplicates
            existing["payloads"].append(payload)
        terminal = packet["phase"] == "completion" and (packet["transfer_type"] != 2
                    or packet["control_stage"] in (2, 3))
        if terminal:
            existing["complete"] = True
            pending.pop(key, None)
    patterns = Counter()
    for transfer in transfers:
        for payload in transfer["payloads"]:
            if (decode_awpro and transfer["complete"] and transfer.get("completion_status") == "0x00000000"
                    and payload["canonical_data_phase"]):
                interpreted = awpro_fields(payload)
                if interpreted:
                    payload["awpro_fields"] = interpreted
            if payload["canonical_data_phase"]:
                patterns[(payload["direction"], payload["length"], payload["hex"],
                          tuple(transfer["scenarios"]), transfer.get("completion_status"))] += 1
    return {"schema_version": 1, "selected_bus": bus, "selected_device_address": device,
            "selected_frame_count": selected_frames, "transfer_count": len(transfers),
            "limitations": ["No live battery percentage or current connection state is inferred.",
                            "Optional AW Pro interpretation requires independently confirmed 413C:A529 / FF13 identity.",
                            "IRP grouping correlates USB submissions/completions, not application command/reply meaning.",
                            "A control setup report ID is evidence; an interrupt payload's first byte alone is not.",
                            "Only control and interrupt traffic is analyzed; audio/isochronous/bulk traffic is omitted."],
            "scenario_evidence": list(markers), "transfers": transfers,
            "payload_patterns": [{"direction": direction, "length": length, "hex": payload,
                                  "scenarios": list(scenarios), "completion_status": status, "count": count}
                                 for (direction, length, payload, scenarios, status), count in patterns.items()]}


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("capture", type=Path, help="USBPcap .pcap/.pcapng or tshark -T json -x export")
    parser.add_argument("--bus", type=int, required=True, help="Observed USB bus number")
    parser.add_argument("--device", type=int, required=True, help="Observed dongle USB address for this capture")
    parser.add_argument("--markers", type=Path, help="Optional timestamped scenario/AWCC observations JSON")
    parser.add_argument("--output", type=Path, help="New analysis JSON file; defaults to stdout")
    parser.add_argument("--tshark", default="tshark", help="Path to installed tshark, used only with -r for offline input")
    parser.add_argument("--decode-awpro", action="store_true",
                        help="Interpret documented monitoring frames after separately confirming dongle 413C:A529 / FF13")
    args = parser.parse_args()
    try:
        if not 0 <= args.bus <= 65535 or not 1 <= args.device <= 127:
            raise ValueError("Bus must be 0..65535; observed USB device address must be 1..127.")
        source = args.capture.resolve(strict=True)
        if source.suffix.lower() == ".json":
            items = json.loads(source.read_text(encoding="utf-8-sig"))
        elif source.suffix.lower() in (".pcap", ".pcapng"):
            display_filter = f"usb.bus_id == {args.bus} && usb.device_address == {args.device} && (usb.transfer_type == 1 || usb.transfer_type == 2)"
            # Fixed read-only arguments: no -i, capture interface, driver, AWCC, or HID access.
            result = subprocess.run([args.tshark, "-r", str(source), "-Y", display_filter, "-T", "json", "-x"],
                                    capture_output=True, text=True, encoding="utf-8", timeout=120, check=True)
            items = json.loads(result.stdout)
        else:
            raise ValueError("Use .pcap, .pcapng, or .json input.")
        if not isinstance(items, list):
            raise ValueError("Expected a JSON packet array.")
        report = analyze(items, args.bus, args.device, load_markers(args.markers), args.decode_awpro)
        if report["selected_frame_count"] == 0:
            raise ValueError("No control/interrupt frames match the selected bus/device; verify the capture address.")
        with source.open("rb") as capture_file:
            report["source_sha256"] = hashlib.file_digest(capture_file, "sha256").hexdigest()
        encoded = json.dumps(report, indent=2) + "\n"
        if args.output:
            with args.output.open("x", encoding="utf-8") as output:
                output.write(encoded)
        else:
            sys.stdout.write(encoded)
        return 0
    except subprocess.CalledProcessError as error:
        print(f"Offline tshark failed ({error.returncode}): {error.stderr.strip()}", file=sys.stderr)
        return 1
    except (OSError, ValueError, KeyError, TypeError, struct.error, subprocess.TimeoutExpired) as error:
        print(f"Analysis failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
