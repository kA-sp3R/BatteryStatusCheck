"""Synthetic, hardware-free tests for the read-only release ZIP size inventory."""

import contextlib
import copy
import hashlib
import importlib.util
import io
import json
from pathlib import Path
import stat
import tempfile
import unittest
import warnings
from unittest.mock import patch
from zipfile import ZIP_DEFLATED, ZipFile, ZipInfo


spec = importlib.util.spec_from_file_location("package_inventory", Path(__file__).with_name("measure-package-size.py"))
inventory = importlib.util.module_from_spec(spec)
spec.loader.exec_module(inventory)


class PackageInventoryTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix="battery-package-inventory-")
        self.directory = Path(self.temporary.name)
        self.package = self.directory / "DeviceBatteryStatus-1.2.0-win-x64-self-contained.zip"
        self.payload = {name: (name + ": synthetic content").encode("ascii") for name in inventory.APPLICATION_FILES}
        self.payload.update({
            "coreclr.dll": b"synthetic-runtime" * 100,
            "PresentationFramework.dll": b"synthetic-desktop" * 200,
            "fr/PresentationFramework.resources.dll": b"synthetic-localization" * 50,
            "licenses/dotnet/LICENSE.txt": b"synthetic runtime license",
            "THIRD_PARTY_NOTICES.md": b"synthetic project notices",
            "README.md": b"synthetic package instructions",
        })
        self.manifest = {
            "schemaVersion": 1, "product": "Device Battery Status", "version": "1.2.0",
            "flavor": "self-contained", "runtimeIdentifier": "win-x64", "sourceCommit": "a" * 40,
            "repository": "owner/repository", "workflowRun": "123", "workflowAttempt": "2", "sdkVersion": "10.0.403",
            "runtimeOptions": {"tfm": "net10.0", "includedFrameworks": [{"name": "Microsoft.WindowsDesktop.App", "version": "10.0.14"}]},
            "applicationLaunched": False, "hardwareVerified": False,
            "files": {name: hashlib.sha256(content).hexdigest() for name, content in self.payload.items()},
        }

    def tearDown(self):
        self.temporary.cleanup()

    def write_package(self, payload=None, manifest=None, extra_entries=None, manifest_text=None):
        payload = self.payload if payload is None else payload
        manifest = self.manifest if manifest is None else manifest
        with warnings.catch_warnings():
            warnings.simplefilter("ignore", UserWarning)
            with ZipFile(self.package, "w", compression=ZIP_DEFLATED) as archive:
                for name, content in payload.items():
                    archive.writestr(name, content)
                archive.writestr("package-manifest.json", manifest_text if manifest_text is not None else json.dumps(manifest))
                for entry, content in extra_entries or []:
                    archive.writestr(entry, content)
        return self.package

    def test_verified_sizes_categories_hashes_and_provenance_are_consistent(self):
        self.write_package()
        before = self.package.read_bytes()
        report = inventory.measure_package(self.package, "a" * 40, 123, 2)
        self.assertEqual(1, report["schemaVersion"])
        self.assertEqual("read-only-windows-package-size-inventory", report["mode"])
        self.assertEqual("a" * 40, report["sourceCommit"])
        self.assertEqual("10.0.403", report["sdkVersion"])
        self.assertEqual(self.manifest["runtimeOptions"], report["runtimeOptions"])
        self.assertTrue(report["payloadHashesVerified"])
        for key in ("applicationLaunched", "hardwareAccessed", "archiveExtracted"):
            self.assertIs(report[key], False)
        self.assertEqual(before, self.package.read_bytes())
        package = report["package"]
        self.assertEqual(len(before), package["bytes"])
        self.assertEqual(hashlib.sha256(before).hexdigest(), package["sha256"])
        self.assertEqual(len(self.payload) + 1, package["entryCount"])
        with ZipFile(self.package) as archive:
            self.assertEqual(sum(entry.file_size for entry in archive.infolist()), package["extractedBytes"])
            self.assertEqual(sum(entry.compress_size for entry in archive.infolist()), package["compressedEntryBytes"])
        self.assertEqual(package["bytes"], package["compressedEntryBytes"] + package["zipOverheadBytes"])
        self.assertEqual(package["extractedBytes"], sum(category["extractedBytes"] for category in report["categories"].values()))
        self.assertEqual(package["entryCount"], sum(category["fileCount"] for category in report["categories"].values()))
        self.assertEqual(6, report["categories"]["app"]["fileCount"])
        self.assertEqual(2, report["categories"]["runtime"]["fileCount"])
        self.assertEqual(1, report["categories"]["localization"]["fileCount"])
        self.assertEqual(2, report["categories"]["notices"]["fileCount"])
        self.assertEqual(2, report["categories"]["metadata"]["fileCount"])
        self.assertEqual(0, report["categories"]["other"]["fileCount"])
        files = {entry["path"]: entry for entry in report["files"]}
        for name, content in self.payload.items():
            self.assertEqual(hashlib.sha256(content).hexdigest(), files[name]["sha256"])
        self.assertEqual("PresentationFramework.dll", report["largestFiles"][0]["path"])

    def test_framework_dependent_unknown_files_are_not_claimed_as_runtime(self):
        manifest = copy.deepcopy(self.manifest)
        manifest["flavor"] = "framework-dependent"
        self.write_package(manifest=manifest)
        report = inventory.measure_package(self.package)
        self.assertEqual(0, report["categories"]["runtime"]["fileCount"])
        self.assertEqual(2, report["categories"]["other"]["fileCount"])

    def test_repeated_inventory_is_deterministic_without_timestamps_or_extraction(self):
        self.write_package()
        first = inventory.measure_package(self.package)
        second = inventory.measure_package(self.package)
        self.assertEqual(first, second)
        self.assertEqual([self.package], list(self.directory.iterdir()))

    def test_mutated_payload_is_rejected_before_measurement(self):
        altered = {**self.payload, "coreclr.dll": b"modified content"}
        self.write_package(payload=altered)
        with self.assertRaisesRegex(inventory.PackageMeasurementError, "checksum mismatch"):
            inventory.measure_package(self.package)

    def test_missing_extra_or_misnamed_manifest_payload_is_rejected(self):
        for operation in ("missing", "extra", "mismatched case"):
            with self.subTest(operation=operation):
                payload = dict(self.payload)
                if operation == "missing":
                    del payload["coreclr.dll"]
                elif operation == "extra":
                    payload["unlisted.txt"] = b"not in manifest"
                else:
                    payload["CORECLR.DLL"] = payload.pop("coreclr.dll")
                self.write_package(payload=payload)
                with self.assertRaisesRegex(inventory.PackageMeasurementError, "exact package payload"):
                    inventory.measure_package(self.package)

    def test_missing_required_application_file_is_rejected_even_when_manifest_matches(self):
        payload = {name: content for name, content in self.payload.items() if name != "DeviceBatteryStatus.exe"}
        manifest = copy.deepcopy(self.manifest)
        del manifest["files"]["DeviceBatteryStatus.exe"]
        self.write_package(payload=payload, manifest=manifest)
        with self.assertRaisesRegex(inventory.PackageMeasurementError, "required application files"):
            inventory.measure_package(self.package)

    def test_duplicate_and_case_colliding_paths_are_rejected(self):
        for name in ("coreclr.dll", "CORECLR.DLL", "PACKAGE-MANIFEST.JSON"):
            with self.subTest(name=name):
                self.write_package(extra_entries=[(name, b"duplicate")])
                with self.assertRaisesRegex(inventory.PackageMeasurementError, "Duplicate or case-colliding"):
                    inventory.measure_package(self.package)

    def test_unsafe_and_windows_ambiguous_paths_are_rejected(self):
        for name in ("../escape.dll", "/absolute.dll", "C:/drive.dll", "a\\b.dll", "a//b.dll", "a/./b.dll",
                     "folder/", "bad\x00name", "bad\nname", "space /name.dll", "dot./name.dll", "CON.dll", "aux/data"):
            with self.subTest(name=name):
                # zipfile truncates a NUL in the ZIP entry name, so exercise its
                # original ambiguous input directly as well as archive paths.
                if "\x00" in name:
                    with self.assertRaises(inventory.PackageMeasurementError):
                        inventory.validate_path(name)
                else:
                    self.write_package(extra_entries=[(name, b"invalid path")])
                    with self.assertRaises(inventory.PackageMeasurementError):
                        inventory.measure_package(self.package)

    def test_symlink_payload_is_rejected(self):
        entry = ZipInfo("linked.dll")
        entry.create_system = 3
        entry.external_attr = (stat.S_IFLNK | 0o777) << 16
        self.write_package(extra_entries=[(entry, b"target.dll")])
        with self.assertRaisesRegex(inventory.PackageMeasurementError, "non-regular"):
            inventory.measure_package(self.package)

    def test_manifest_duplicate_json_keys_cannot_hide_payload_changes(self):
        text = json.dumps(self.manifest)
        text = text[:-1] + ', "sourceCommit": "' + "b" * 40 + '"}'
        self.write_package(manifest_text=text)
        with self.assertRaisesRegex(inventory.PackageMeasurementError, "duplicate JSON keys"):
            inventory.measure_package(self.package)

    def test_missing_malformed_or_wrong_schema_manifest_is_rejected(self):
        with ZipFile(self.package, "w") as archive:
            archive.writestr("not-a-manifest.txt", b"synthetic")
        with self.assertRaisesRegex(inventory.PackageMeasurementError, "manifest is missing"):
            inventory.measure_package(self.package)
        for text in ("{", "null", "[]", '{"schemaVersion":true}', '{"schemaVersion":NaN}'):
            with self.subTest(text=text):
                self.write_package(manifest_text=text)
                with self.assertRaises(inventory.PackageMeasurementError):
                    inventory.measure_package(self.package)

    def test_invalid_provenance_and_claims_are_rejected(self):
        for field, value in (("sourceCommit", "short"), ("workflowRun", 123), ("workflowAttempt", "0"),
                             ("repository", "no-owner"), ("sdkVersion", None), ("flavor", "other"),
                             ("runtimeIdentifier", "win-arm64"), ("runtimeOptions", None),
                             ("applicationLaunched", True), ("hardwareVerified", True), ("files", None)):
            with self.subTest(field=field):
                self.write_package(manifest={**self.manifest, field: value})
                with self.assertRaises(inventory.PackageMeasurementError):
                    inventory.measure_package(self.package)

    def test_expected_source_workflow_and_attempt_must_match(self):
        self.write_package()
        for expected in ({"expected_source": "b" * 40}, {"expected_workflow_run": "124"}, {"expected_workflow_attempt": 1}):
            with self.subTest(expected=expected), self.assertRaisesRegex(inventory.PackageMeasurementError, "expected provenance"):
                inventory.measure_package(self.package, **expected)

    def test_invalid_manifest_hash_is_rejected(self):
        for digest in (None, "A" * 64, "short"):
            with self.subTest(digest=digest):
                manifest = copy.deepcopy(self.manifest)
                manifest["files"]["coreclr.dll"] = digest
                self.write_package(manifest=manifest)
                with self.assertRaisesRegex(inventory.PackageMeasurementError, "payload SHA-256"):
                    inventory.measure_package(self.package)

    def test_entry_payload_and_manifest_size_limits_are_enforced_before_reading(self):
        self.write_package()
        for name, value in (("MAXIMUM_ENTRY_BYTES", 1), ("MAXIMUM_PAYLOAD_BYTES", 1), ("MAXIMUM_MANIFEST_BYTES", 1)):
            with self.subTest(limit=name), patch.object(inventory, name, value), self.assertRaisesRegex(inventory.PackageMeasurementError, "(limit|too large)"):
                inventory.measure_package(self.package)

    def test_corrupt_zip_is_reported_as_inventory_failure(self):
        self.package.write_bytes(b"not a ZIP")
        with self.assertRaises(inventory.PackageMeasurementError):
            inventory.measure_package(self.package)

    def test_cli_writes_only_requested_json_and_never_overwrites_input(self):
        self.write_package()
        before = self.package.read_bytes()
        output = self.directory / "evidence" / "size.json"
        result = inventory.main(["--package", str(self.package), "--output", str(output),
                                 "--expected-source", "a" * 40, "--expected-run", "123", "--expected-attempt", "2"])
        self.assertEqual(0, result)
        self.assertTrue(json.loads(output.read_text(encoding="utf-8"))["payloadHashesVerified"])
        stderr = io.StringIO()
        with contextlib.redirect_stderr(stderr):
            result = inventory.main(["--package", str(self.package), "--output", str(self.package)])
        self.assertEqual(1, result)
        self.assertIn("must not overwrite", stderr.getvalue())
        self.assertEqual(before, self.package.read_bytes())


if __name__ == "__main__":
    unittest.main()
