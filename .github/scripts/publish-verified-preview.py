"""Publish only the exact packages and passing test evidence in a reviewed manifest."""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.parse
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZIP_DEFLATED


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def gh(*args):
    result = subprocess.run(["gh", *map(str, args)], capture_output=True, text=True)
    if result.returncode:
        error = re.sub(r"https?://\S+", lambda m: urllib.parse.urlunsplit(
            (*urllib.parse.urlsplit(m[0])[:3], "", "")), result.stderr)
        raise SystemExit(error.strip() or f"gh exited {result.returncode}")
    return result.stdout


repository = os.environ["GITHUB_REPOSITORY"]
version = os.environ["RELEASE_VERSION"]
require(re.fullmatch(r"\d+\.\d+\.\d+-preview\.\d+", version), "Invalid preview version.")
manifest_path = Path(".github/releases") / f"{version}.json"
manifest = json.loads(manifest_path.read_text())
require(manifest["version"] == version, "Manifest version mismatch.")
tag = manifest["tag"]
require(tag == f"v{version}", "Manifest tag mismatch.")
source = manifest["sourceCommit"]
require(re.fullmatch(r"[0-9a-f]{40}", source), "Invalid source commit.")
api_root = f"repos/{repository}"


def api(path):
    return json.loads(gh("api", f"{api_root}/{path}"))


build_run = api(f"actions/runs/{manifest['buildRun']}")
test_run = api(f"actions/runs/{manifest['fullTestRun']}")
require(build_run["conclusion"] == "success" and build_run["head_sha"] == source,
        "Package build did not pass for the configured source.")
require(test_run["conclusion"] == "success" and
        test_run["head_sha"] == manifest["testAutomationCommit"] and
        test_run["path"] == ".github/workflows/full-software-tests.yml",
        "Full test automation run did not match the reviewed successful run.")
release = api(f"releases/{manifest['releaseId']}")
require(release["draft"] and release["prerelease"] and release["tag_name"] == tag and
        release["target_commitish"] == source, "Release draft or source mismatch.")
notes = (manifest_path.with_suffix(".md")).read_text()
require(release["body"].strip() == notes.strip(), "Draft notes differ from the reviewed release notes.")
staging = Path("release-staging")
evidence = staging / "evidence"
assets = []


def download(run, name, directory):
    directory.mkdir(parents=True, exist_ok=True)
    gh("run", "download", run, "--repo", repository, "--name", name, "--dir", directory)


for flavor, expected in manifest["packages"].items():
    destination = staging / flavor
    download(manifest["buildRun"], f"windows-preview-win-x64-{flavor}-1", destination)
    package = destination / f"DeviceBatteryStatus-{version}-win-x64-{flavor}.zip"
    checksum = package.with_suffix(".sha256")
    digest = hashlib.sha256(package.read_bytes()).hexdigest()
    require(package.stat().st_size == expected["bytes"] and digest == expected["sha256"],
            f"Unexpected package bytes: {flavor}")
    require(checksum.read_text(encoding="utf-8-sig").split()[0].lower() == digest,
            f"Checksum mismatch: {flavor}")
    with ZipFile(package) as archive:
        package_manifest = json.loads(archive.read("package-manifest.json"))
        require(package_manifest["sourceCommit"] == source and package_manifest["version"] == version and
                package_manifest["workflowRun"] == str(manifest["buildRun"]), "Package provenance mismatch.")
        require(package_manifest["applicationLaunched"] is False and package_manifest["hardwareVerified"] is False,
                "Unexpected runtime/hardware verification claim.")
        require(set(archive.namelist()) == set(package_manifest["files"]) | {"package-manifest.json"},
                "Unexpected ZIP payload.")
        for name, expected_hash in package_manifest["files"].items():
            require(hashlib.sha256(archive.read(name)).hexdigest() == expected_hash,
                    f"Payload hash mismatch: {name}")
    assets.extend([package, checksum])

test_results = {}
for configuration in ["Debug", "Release"]:
    destination = evidence / configuration
    download(manifest["fullTestRun"], f"full-software-tests-{configuration}-1", destination)
    report = json.loads(next(destination.rglob(f"full-{configuration}.json")).read_text(encoding="utf-8-sig"))
    require(report["sourceCommit"] == source and report["configuration"] == configuration and
            report["nativeExit"] == 0, "Full test provenance mismatch.")
    expected_counts = {"total": manifest["testCount"], "executed": manifest["testCount"],
                       "passed": manifest["testCount"], "failed": 0, "notExecuted": 0}
    require(report["counters"] == expected_counts, "Full test counters do not match.")
    require(report["applicationLaunched"] is False and report["hardwareVerified"] is False,
            "Unexpected full test verification claim.")
    reports = list(destination.rglob("*.trx"))
    require(len(reports) == report["reportCount"] == len(report["testProjects"]), "Missing test project reports.")
    counts = dict.fromkeys(expected_counts, 0)
    for trx in reports:
        counter = ET.parse(trx).getroot().find("{*}ResultSummary/{*}Counters")
        require(counter is not None, "Missing TRX counters.")
        for key in counts:
            counts[key] += int(counter.attrib[key])
    require(counts == expected_counts, "TRX counters do not match the summary.")
    test_results[configuration] = counts

download(manifest["fullTestRun"], "full-offline-parser-tests-1", evidence / "offline")
offline = next((evidence / "offline").rglob("*.log")).read_text(encoding="utf-8-sig")
require(re.search(r"Ran 5 tests", offline) and re.search(r"(?m)^OK\s*$", offline) and
        "native_exit_code=0" in offline, "Offline parser evidence did not pass.")
test_results["offlineParser"] = {"executed": 5, "passed": 5, "failed": 0, "skipped": 0}
summary = {**manifest, "testResults": test_results, "applicationLaunched": False, "hardwareVerified": False,
           "publicationWorkflowCommit": os.environ["GITHUB_SHA"],
           "publicationWorkflowRun": os.environ["GITHUB_RUN_ID"]}
(evidence / "verification-summary.json").write_text(json.dumps(summary, indent=2) + "\n")
(evidence / "VERIFICATION.md").write_text(notes)
verification_dir = staging / "verification"
verification_dir.mkdir(parents=True, exist_ok=True)
verification = verification_dir / f"DeviceBatteryStatus-{version}-verification.zip"
with ZipFile(verification, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
    for file in sorted(evidence.rglob("*")):
        if file.is_file():
            archive.write(file, file.relative_to(evidence).as_posix())
verification_checksum = verification.with_suffix(".sha256")
verification_checksum.write_text(f"{hashlib.sha256(verification.read_bytes()).hexdigest()}  {verification.name}\n")
assets.extend([verification, verification_checksum])
expected_assets = {file.name: {"size": file.stat().st_size, "digest": "sha256:" + hashlib.sha256(file.read_bytes()).hexdigest()}
                   for file in assets}
existing_assets = {asset["name"]: asset for asset in release["assets"]}
require(set(existing_assets).issubset(expected_assets), "Draft contains unexpected assets.")
for name, asset in existing_assets.items():
    require(asset["state"] == "uploaded" and all(asset[key] == value for key, value in expected_assets[name].items()),
            f"Existing asset does not match: {name}")
missing = [file for file in assets if file.name not in existing_assets]
if missing:
    gh("release", "upload", tag, *missing, "--repo", repository)
release = api(f"releases/{manifest['releaseId']}")
require(len(release["assets"]) == len(expected_assets), "Release assets are incomplete.")
for asset in release["assets"]:
    require(asset["state"] == "uploaded" and asset["name"] in expected_assets and
            all(asset[key] == value for key, value in expected_assets[asset["name"]].items()),
            f"Uploaded asset verification failed: {asset['name']}")
# Check any existing tag before publishing; a missing tag will be created at the draft's source commit.
tags = json.loads(gh("api", "--paginate", "--slurp", f"{api_root}/git/matching-refs/tags/{tag}"))
for page in tags:
    for ref in page:
        if ref["ref"] == f"refs/tags/{tag}":
            require(ref["object"]["type"] == "commit" and ref["object"]["sha"] == source,
                    "Existing release tag does not point to the verified source.")
gh("release", "edit", tag, "--repo", repository, "--draft=false", "--prerelease", "--latest=false")
published = api(f"releases/{manifest['releaseId']}")
tag_ref = api(f"git/ref/tags/{tag}")
require(not published["draft"] and published["prerelease"] and published["published_at"] and
        tag_ref["object"]["type"] == "commit" and tag_ref["object"]["sha"] == source,
        "Published release or tag verification failed.")
receipt = {"releaseUrl": published["html_url"], "releaseId": published["id"], "tag": tag,
           "sourceCommit": source, "publishedAt": published["published_at"], "prerelease": published["prerelease"],
           "fullTestRun": manifest["fullTestRun"], "testResults": test_results, "assets": expected_assets}
(staging / "publication-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(f"::notice title=Published verified preview::{published['html_url']}; Source={source}; Assets={len(expected_assets)}; Debug=451 passed; Release=451 passed; Parser=5 passed; ApplicationLaunched=false; HardwareVerified=false")
