"""Inventory an already-built Windows package without extracting or executing it.

Every manifest payload hash is checked before reporting sizes. The resulting JSON
is size evidence, not compilation, application-runtime or hardware verification.
"""

from __future__ import annotations

import argparse
import hashlib
import json
import re
import stat
import sys
from pathlib import Path
from zipfile import BadZipFile, ZipFile


SCHEMA_VERSION = 1
MAXIMUM_ENTRY_BYTES = 512 * 1024 * 1024
MAXIMUM_PAYLOAD_BYTES = 2 * 1024 * 1024 * 1024
MAXIMUM_MANIFEST_BYTES = 4 * 1024 * 1024
APPLICATION_FILES = frozenset({
    "DeviceBatteryStatus.exe", "DeviceBatteryStatus.dll", "DeviceBatteryStatus.Core.dll",
    "DeviceBatteryStatus.Windows.dll", "DeviceBatteryStatus.deps.json",
    "DeviceBatteryStatus.runtimeconfig.json",
})
CATEGORIES = ("app", "runtime", "localization", "notices", "metadata", "other")
WINDOWS_RESERVED_NAMES = frozenset({"con", "prn", "aux", "nul"} |
    {f"{prefix}{number}" for prefix in ("com", "lpt") for number in range(1, 10)})


class PackageMeasurementError(ValueError):
    """The archive cannot provide trustworthy package-size evidence."""


def require(condition: bool, message: str) -> None:
    if not condition:
        raise PackageMeasurementError(message)


def validate_path(name: str) -> None:
    require(isinstance(name, str) and bool(name), "Empty or non-string ZIP path.")
    require(not any(ord(character) < 32 for character in name) and
            not any(character in name for character in "\\:*?\"<>|"), "Unsafe ZIP path.")
    parts = name.split("/")
    require(all(part and part not in (".", "..") and not part.endswith((" ", "."))
                and part.split(".", 1)[0].casefold() not in WINDOWS_RESERVED_NAMES
                for part in parts), "Unsafe or Windows-ambiguous ZIP path.")


def unique_json_object(pairs):
    result = {}
    for key, value in pairs:
        require(key not in result, "Package manifest contains duplicate JSON keys.")
        result[key] = value
    return result


def reject_json_constant(value):
    raise PackageMeasurementError(f"Non-finite JSON constant in package manifest: {value}")


def sha256_stream(stream) -> str:
    digest = hashlib.sha256()
    while block := stream.read(1024 * 1024):
        digest.update(block)
    return digest.hexdigest()


def category_for(path: str, flavor: str) -> str:
    if path in APPLICATION_FILES:
        return "app"
    if path.startswith("licenses/") or path == "THIRD_PARTY_NOTICES.md":
        return "notices"
    if path in ("package-manifest.json", "README.md"):
        return "metadata"
    if "/" in path and path.endswith(".resources.dll"):
        return "localization"
    if flavor == "self-contained" and "/" not in path and path.endswith((".dll", ".exe")):
        return "runtime"
    return "other"


def measure_package(package_path: Path | str, expected_source: str | None = None,
                    expected_workflow_run: str | int | None = None,
                    expected_workflow_attempt: str | int | None = None) -> dict:
    """Return verified sizes and provenance; reject unsafe or inconsistent ZIPs."""
    package_path = Path(package_path)
    try:
        with package_path.open("rb") as package_stream:
            package_stat = package_path.stat()
            archive_hash = sha256_stream(package_stream)
            package_stream.seek(0)
            with ZipFile(package_stream) as archive:
                entries = archive.infolist()
                require(bool(entries), "The package ZIP is empty.")
                seen = set()
                total_bytes = 0
                for entry in entries:
                    validate_path(entry.filename)
                    require(not entry.is_dir(), "ZIP directory entries are not package payload files.")
                    folded = entry.filename.casefold()
                    require(folded not in seen, "Duplicate or case-colliding ZIP path.")
                    seen.add(folded)
                    mode = entry.external_attr >> 16
                    require(stat.S_IFMT(mode) in (0, stat.S_IFREG), "ZIP contains a non-regular payload file.")
                    require(not entry.flag_bits & 1, "Encrypted ZIP entries cannot be package evidence.")
                    require(0 <= entry.file_size <= MAXIMUM_ENTRY_BYTES and entry.compress_size >= 0,
                            "ZIP entry is outside the supported size limits.")
                    total_bytes += entry.file_size
                    require(total_bytes <= MAXIMUM_PAYLOAD_BYTES, "ZIP payload exceeds the supported size limit.")
                require("package-manifest.json" in archive.namelist(), "Package manifest is missing.")
                manifest_entry = archive.getinfo("package-manifest.json")
                require(manifest_entry.file_size <= MAXIMUM_MANIFEST_BYTES, "Package manifest is too large.")
                manifest = json.loads(archive.read(manifest_entry).decode("utf-8-sig"),
                                      object_pairs_hook=unique_json_object, parse_constant=reject_json_constant)
                require(isinstance(manifest, dict) and type(manifest.get("schemaVersion")) is int and manifest.get("schemaVersion") == 1,
                        "Unsupported package manifest schema.")
                require(manifest.get("product") == "Device Battery Status", "Unexpected package product.")
                require(manifest.get("flavor") in ("framework-dependent", "self-contained") and
                        manifest.get("runtimeIdentifier") == "win-x64", "Unexpected package flavor or architecture.")
                for key, pattern in (("version", r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?"),
                                     ("sourceCommit", r"[0-9a-f]{40}"),
                                     ("workflowRun", r"[1-9][0-9]*"), ("workflowAttempt", r"[1-9][0-9]*"),
                                     ("sdkVersion", r"\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?"),
                                     ("repository", r"[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+")):
                    require(isinstance(manifest.get(key), str) and re.fullmatch(pattern, manifest[key]) is not None,
                            f"Invalid package provenance field: {key}.")
                require(isinstance(manifest.get("runtimeOptions"), dict), "Package runtime metadata is missing.")
                require(manifest.get("applicationLaunched") is False and manifest.get("hardwareVerified") is False,
                        "Unexpected package-stage runtime or hardware claim.")
                for key, expected in (("sourceCommit", expected_source), ("workflowRun", expected_workflow_run),
                                      ("workflowAttempt", expected_workflow_attempt)):
                    if expected is not None:
                        require(manifest[key] == str(expected), f"Package {key} does not match the expected provenance.")
                expected_files = manifest.get("files")
                require(isinstance(expected_files, dict), "Package payload hashes are missing.")
                for name, digest in expected_files.items():
                    validate_path(name)
                    require(isinstance(digest, str) and re.fullmatch(r"[0-9a-f]{64}", digest) is not None,
                            "Invalid package payload SHA-256.")
                require(set(expected_files) == set(archive.namelist()) - {"package-manifest.json"},
                        "Manifest does not enumerate the exact package payload.")
                require(APPLICATION_FILES <= set(expected_files), "Package is missing required application files.")
                records = []
                for entry in sorted(entries, key=lambda value: value.filename.encode("utf-8")):
                    with archive.open(entry) as content:
                        digest = sha256_stream(content)
                    require(entry.filename == "package-manifest.json" or digest == expected_files[entry.filename],
                            f"Package payload checksum mismatch: {entry.filename}")
                    records.append({
                        "path": entry.filename, "extractedBytes": entry.file_size,
                        "compressedBytes": entry.compress_size, "compressionMethod": entry.compress_type,
                        "sha256": digest, "category": category_for(entry.filename, manifest["flavor"]),
                    })
                compressed_bytes = sum(record["compressedBytes"] for record in records)
                require(compressed_bytes <= package_stat.st_size, "ZIP compressed sizes exceed the archive size.")
                after = package_path.stat()
                require((after.st_size, after.st_mtime_ns, after.st_ino) ==
                        (package_stat.st_size, package_stat.st_mtime_ns, package_stat.st_ino),
                        "Package changed while it was being inventoried.")
    except (BadZipFile, UnicodeError, json.JSONDecodeError, NotImplementedError, RuntimeError) as error:
        raise PackageMeasurementError(f"Cannot inventory package ZIP: {error}") from error

    categories = {}
    for category in CATEGORIES:
        group = [record for record in records if record["category"] == category]
        categories[category] = {
            "fileCount": len(group), "extractedBytes": sum(record["extractedBytes"] for record in group),
            "compressedBytes": sum(record["compressedBytes"] for record in group),
        }
    return {
        "schemaVersion": SCHEMA_VERSION, "mode": "read-only-windows-package-size-inventory",
        "appVersion": manifest["version"], "sourceCommit": manifest["sourceCommit"],
        "repository": manifest["repository"], "workflowRun": manifest["workflowRun"],
        "workflowAttempt": manifest["workflowAttempt"], "sdkVersion": manifest["sdkVersion"],
        "flavor": manifest["flavor"], "runtimeIdentifier": manifest["runtimeIdentifier"],
        "runtimeOptions": manifest["runtimeOptions"], "payloadHashesVerified": True,
        "applicationLaunched": False, "hardwareAccessed": False, "archiveExtracted": False,
        "package": {"name": package_path.name, "bytes": package_stat.st_size, "sha256": archive_hash,
                    "entryCount": len(records), "extractedBytes": total_bytes,
                    "compressedEntryBytes": compressed_bytes, "zipOverheadBytes": package_stat.st_size - compressed_bytes},
        "categories": categories,
        "largestFiles": sorted(records, key=lambda record: (-record["extractedBytes"], record["path"]))[:20],
        "files": records,
    }


def main(argv=None) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--package", type=Path, required=True)
    parser.add_argument("--output", type=Path)
    parser.add_argument("--expected-source")
    parser.add_argument("--expected-run", "--expected-workflow-run", dest="expected_run")
    parser.add_argument("--expected-attempt", "--expected-workflow-attempt", dest="expected_attempt")
    arguments = parser.parse_args(argv)
    try:
        require(arguments.output is None or arguments.output.resolve() != arguments.package.resolve(),
                "Size report must not overwrite the package archive.")
        report = measure_package(arguments.package, arguments.expected_source,
                                 arguments.expected_run, arguments.expected_attempt)
        output = json.dumps(report, ensure_ascii=False, indent=2) + "\n"
        if arguments.output is None:
            sys.stdout.write(output)
        else:
            arguments.output.parent.mkdir(parents=True, exist_ok=True)
            arguments.output.write_text(output, encoding="utf-8")
        return 0
    except (PackageMeasurementError, OSError) as error:
        print(f"Package size inventory failed: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
