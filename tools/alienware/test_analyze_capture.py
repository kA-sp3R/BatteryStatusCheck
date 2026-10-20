"""Synthetic offline parser checks only; these do not verify hardware or battery semantics."""
import importlib.util
from pathlib import Path
import struct
import unittest

spec = importlib.util.spec_from_file_location("analyze_capture", Path(__file__).with_name("analyze_capture.py"))
analyzer = importlib.util.module_from_spec(spec)
spec.loader.exec_module(analyzer)


def packet(frame, irp, payload=b"", *, complete=False, endpoint=0x81, transfer=1,
           stage=None, device=7, status=0):
    header_length = 28 if transfer == 2 else 27
    header = struct.pack("<HQIHBHHBBI", header_length, irp, status, 9, int(complete),
                         1, device, endpoint, transfer, len(payload))
    if transfer == 2:
        header += bytes([stage])
    return {"frame": frame, "time_epoch": frame, "usbpcap_raw_hex": (header + payload).hex()}


class OfflineAnalysisTests(unittest.TestCase):
    def test_submit_completion_same_payload_counted_once_preserves_zero(self):
        report = analyzer.analyze([packet(1, 12, b"\x00\x00"),
                                   packet(2, 12, b"\x00\x00", complete=True)], 1, 7)
        self.assertEqual(report["transfer_count"], 1)
        self.assertEqual(report["payload_patterns"][0]["count"], 1)
        payloads = report["transfers"][0]["payloads"]
        self.assertEqual(payloads[1]["same_transfer_duplicate_of_frames"], [1])
        self.assertEqual(payloads[1]["first_payload_byte"], 0)
        self.assertIsNone(payloads[1]["report_id"])

    def test_hid_setup_report_id_and_irp_reuse(self):
        setup = struct.pack("<BBHHH", 0xA1, 1, 0x0107, 3, 63)
        packets = [packet(1, 11, setup, endpoint=0x80, transfer=2, stage=0),
                   packet(2, 11, b"\x07\xc0\x00", complete=True, endpoint=0x80, transfer=2, stage=3),
                   packet(3, 11, b"\x00", complete=True)]
        report = analyzer.analyze(packets, 1, 7)
        self.assertEqual(report["transfer_count"], 2)
        self.assertEqual(report["transfers"][0]["payloads"][0]["report_id"], 7)
        self.assertEqual(report["transfers"][0]["setup"]["interface_or_index"], 3)
        self.assertTrue(report["transfers"][1]["warnings"])

    def test_wrong_device_filtered_and_error_completion_kept(self):
        report = analyzer.analyze([packet(1, 10, b"\x19", device=8),
                                   packet(2, 11, b"\x00", complete=True, status=0xC0000001)], 1, 7)
        self.assertEqual(report["selected_frame_count"], 1)
        self.assertEqual(report["transfers"][0]["completion_status"], "0xc0000001")
        self.assertTrue(report["transfers"][0]["warnings"])

    def test_truncated_packet_rejected(self):
        item = packet(1, 1, b"\x00\x01")
        item["usbpcap_raw_hex"] = item["usbpcap_raw_hex"][:-2]
        with self.assertRaises(ValueError):
            analyzer.decode_packet(item, 1)

    def test_optional_awpro_zero_battery_and_invalid_range(self):
        setup = struct.pack("<BBHHH", 0xA1, 1, 0x0107, 3, 63)
        reply = bytes.fromhex("07c00a0a00000000") + bytes(55)
        packets = [packet(1, 11, setup, endpoint=0x80, transfer=2, stage=0),
                   packet(2, 11, reply, complete=True, endpoint=0x80, transfer=2, stage=3)]
        report = analyzer.analyze(packets, 1, 7, decode_awpro=True)
        decoded = report["transfers"][0]["payloads"][0]["awpro_fields"]
        self.assertEqual(decoded["battery_percentage_field"], 0)
        self.assertIn("historical", decoded["freshness"])
        invalid = bytearray(reply)
        invalid[6] = 101
        self.assertIsNone(analyzer.awpro_fields({"hex": invalid.hex(), "direction": "in"}))


if __name__ == "__main__":
    unittest.main()
