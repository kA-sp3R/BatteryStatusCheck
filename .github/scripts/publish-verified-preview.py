"""Publish only the exact packages and passing test evidence in a reviewed manifest.

The historical filename remains compatible with the original preview workflow.
Stable releases require an explicit manifest policy; publishing never signs a binary.
"""
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import urllib.parse
import xml.etree.ElementTree as ET
from zipfile import ZipFile, ZipInfo, ZIP_DEFLATED


VERSION_PATTERN = r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)(?:-preview\.(?:0|[1-9]\d*))?"


def require(condition, message):
    if not condition:
        raise SystemExit(message)


def release_policy(manifest, version, notes):
    require(re.fullmatch(VERSION_PATTERN, version), "Invalid release version.")
    require(manifest["version"] == version and manifest["tag"] == f"v{version}",
            "Manifest version or tag mismatch.")
    preview_version = "-" in version
    # Preserve old preview manifests. A stable release must explicitly opt out of prerelease status.
    prerelease = manifest.get("prerelease", True)
    latest = manifest.get("latest", not prerelease)
    signed = manifest.get("signed", False)
    require(type(prerelease) is bool and type(latest) is bool and signed is False,
            "Explicit boolean release policy is required; this publisher supports unsigned packages only.")
    require(prerelease == preview_version, "Release version and prerelease policy disagree.")
    require(not (prerelease and latest), "A prerelease cannot be marked latest.")
    if not preview_version:
        require(manifest.get("prerelease") is False and manifest.get("signed") is False,
                "Stable publication requires explicit prerelease:false and signed:false.")
    require(re.search(r"\bunsigned\b", notes, re.IGNORECASE), "Release notes must disclose unsigned packages.")
    for field, default in (("testCount", None), ("parserTestCount", 5),
                           ("buildAttempt", 1), ("fullTestAttempt", 1)):
        value = manifest.get(field, default)
        require(type(value) is int and value > 0, f"Invalid manifest count: {field}")
    if "automationTestCount" in manifest:
        require(type(manifest["automationTestCount"]) is int and manifest["automationTestCount"] > 0,
                "Invalid manifest count: automationTestCount")
    if "appSmoke" in manifest:
        require(type(manifest["appSmoke"]) is bool, "Invalid application smoke policy.")
    if "appSmokeCheckCount" in manifest:
        require(type(manifest["appSmokeCheckCount"]) is int and manifest["appSmokeCheckCount"] > 0,
                "Invalid manifest count: appSmokeCheckCount")
    require(manifest.get("appSmokeMode", "embedded") in ("embedded", "companion"), "Invalid smoke mode.")
    if manifest.get("appSmokeMode") == "companion":
        require(manifest.get("appSmoke") is True, "Companion verification must be enabled.")
        hashes = manifest.get("appSmokeAssemblyHashes", {})
        names = {"DeviceBatteryStatus.dll", "DeviceBatteryStatus.Core.dll", "DeviceBatteryStatus.Windows.dll"}
        require(isinstance(hashes, dict) and set(hashes) == {"framework-dependent", "self-contained"} and
                all(isinstance(values, dict) and set(values) == names and
                    all(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) for value in values.values())
                    for values in hashes.values()), "Missing exact companion assembly hashes.")
    if "installer" in manifest:
        installer = manifest["installer"]
        require(isinstance(installer, dict) and type(installer.get("bytes")) is int and installer["bytes"] > 0 and
                isinstance(installer.get("sha256"), str) and re.fullmatch(r"[0-9a-f]{64}", installer["sha256"]),
                "Invalid installer manifest bytes or SHA-256.")
    return {"prerelease": prerelease, "latest": latest, "signed": False}


def offline_test_counts(log, expected):
    matches = re.findall(r"(?m)^Ran (\d+) tests?\b", log)
    require(len(matches) == 1 and int(matches[0]) == expected and
            re.search(r"(?m)^OK\s*$", log) and not re.search(r"(?m)^FAILED\b", log) and
            re.search(r"(?m)^native_exit_code=0\s*$", log), "Offline parser evidence did not pass.")
    return {"executed": expected, "passed": expected, "failed": 0, "skipped": 0}



def single_json(directory, filename):
    files = list(directory.rglob(filename))
    require(len(files) == 1, f"Expected exactly one {filename} evidence file.")
    return json.loads(files[0].read_text(encoding="utf-8-sig"))


def validate_installer_metadata(report, manifest, repository):
    version, source = manifest["version"], manifest["sourceCommit"]
    run, attempt = str(manifest["buildRun"]), str(manifest.get("buildAttempt", 1))
    require(type(report.get("schemaVersion")) is int and report["schemaVersion"] == 1 and
            report.get("product") == "Device Battery Status" and report.get("version") == version and
            report.get("runtimeIdentifier") == "win-x64" and report.get("distribution") == "per-user-installer" and
            report.get("signed") is False and report.get("sourceCommit") == source and
            report.get("repository") == repository and report.get("workflowRun") == run and
            report.get("workflowAttempt") == attempt and report.get("applicationLaunched") is False and
            report.get("hardwareVerified") is False and report.get("appId") == "D38FB615-37DB-4AC0-92CE-BD1F947B9732",
            "Installer build provenance mismatch.")
    expected_package = manifest["packages"]["self-contained"]
    package = report.get("package", {})
    require(package.get("name") == f"DeviceBatteryStatus-{version}-win-x64-self-contained.zip" and
            package.get("bytes") == expected_package["bytes"] and package.get("sha256") == expected_package["sha256"] and
            package.get("workflowRun") == run and package.get("workflowAttempt") == attempt,
            "Installer payload does not match the verified self-contained package.")
    installer = report.get("installer", {})
    require(installer.get("name") == f"DeviceBatteryStatus-{version}-win-x64-setup.exe" and
            installer.get("bytes") == manifest["installer"]["bytes"] and
            installer.get("sha256") == manifest["installer"]["sha256"], "Installer artifact metadata mismatch.")
    compiler = report.get("compiler", {})
    require(compiler.get("product") == "Inno Setup" and
            isinstance(compiler.get("version"), str) and re.fullmatch(r"6\.\d+\.\d+(?:\.\d+)?", compiler["version"]) and
            isinstance(compiler.get("sha256"), str) and re.fullmatch(r"[0-9a-f]{64}", compiler["sha256"]),
            "Missing installer compiler provenance.")


def validate_installer_smoke(report, manifest):
    require(type(report.get("schemaVersion")) is int and report["schemaVersion"] == 1 and
            report.get("passed") is True and report.get("sourceCommit") == manifest["sourceCommit"] and
            report.get("version") == manifest["version"] and report.get("workflowRun") == str(manifest["buildRun"]) and
            report.get("workflowAttempt") == str(manifest.get("buildAttempt", 1)) and
            report.get("installerSha256") == manifest["installer"]["sha256"] and
            report.get("installerBytes") == manifest["installer"]["bytes"], "Installer smoke provenance mismatch.")
    checks = report.get("checks")
    count = report.get("checkCount")
    require(type(count) is int and count > 0 and isinstance(checks, list) and len(checks) == count and
            all(isinstance(check, str) and check.strip() for check in checks) and len(set(checks)) == count,
            "Installer smoke has missing or ambiguous checks.")
    for field in ["initialInstallPassed", "upgradePassed", "ownedStartupUninstallPassed", "foreignStartupUninstallPassed",
                  "settingsPreserved", "cleanUninstall", "installerExecuted"]:
        require(report.get(field) is True, f"Installer smoke did not establish {field}.")
    require(report.get("applicationLaunched") is False and report.get("hardwareVerified") is False,
            "Unexpected installer smoke application/hardware claim.")
    return {"executed": count, "passed": count, "failed": 0, "installerExecuted": True,
            "applicationLaunched": False, "hardwareVerified": False}


def validate_application_smoke(report, manifest, repository, flavor):
    companion = manifest.get("appSmokeMode", "embedded") == "companion"
    require(type(report.get("schemaVersion")) is int and report["schemaVersion"] == (2 if companion else 1) and
            report.get("mode") == ("hardware-free-windows-companion-smoke" if companion
                                   else "hardware-free-windows-application-smoke") and report.get("passed") is True and
            report.get("appVersion") == manifest["version"] and report.get("sourceCommit") == manifest["sourceCommit"] and
            report.get("repository") == repository and report.get("workflowRun") == str(manifest["buildRun"]) and
            report.get("workflowAttempt") == str(manifest.get("buildAttempt", 1)) and report.get("flavor") == flavor and
            report.get("runtimeIdentifier") == "win-x64" and report.get("packagePayloadVerified") is True,
            "Application smoke provenance or package verification mismatch.")
    expected = manifest.get("appSmokeCheckCount", 8)
    counts = {"total": expected, "executed": expected, "passed": expected, "failed": 0}
    require(isinstance(report.get("counters"), dict) and report["counters"] == counts and
            all(type(value) is int for value in report["counters"].values()), "Application smoke counters do not match.")
    checks = report.get("checks")
    require(isinstance(checks, list) and len(checks) == expected and
            all(isinstance(check, dict) and isinstance(check.get("name"), str) and check["name"].strip() and
                check.get("passed") is True for check in checks) and len({check["name"] for check in checks}) == expected,
            "Application smoke has missing, duplicate or failed checks.")
    require(report.get("applicationLaunched") is (False if companion else True) and report.get("normalApplicationControllerStarted") is False and
            report.get("hardwareAccessed") is False and report.get("networkAccessed") is False and
            report.get("startupRegistrationAccessed") is False and report.get("userSettingsPersisted") is False,
            "Unexpected application smoke scope claim.")
    result = {**counts, "applicationLaunched": not companion, "normalApplicationControllerStarted": False,
              "hardwareAccessed": False, "networkAccessed": False}
    if companion:
        expected = manifest.get("appSmokeAssemblyHashes", {}).get(flavor)
        require(isinstance(expected, dict) and set(expected) ==
                {"DeviceBatteryStatus.dll", "DeviceBatteryStatus.Core.dll", "DeviceBatteryStatus.Windows.dll"} and
                all(isinstance(value, str) and re.fullmatch(r"[0-9a-f]{64}", value) for value in expected.values()) and
                report.get("loadedApplicationAssemblies") == expected and
                report.get("harnessSourceCommit") == manifest["sourceCommit"] and
                report.get("harnessLaunched") is True and report.get("harnessExecuted") is True and
                report.get("separateHarnessVerified") is True and report.get("packagedApplicationAssembliesLoaded") is True,
                "Companion harness or packaged assembly provenance mismatch.")
        result.update(harnessLaunched=True, packagedApplicationAssembliesLoaded=True,
                      loadedApplicationAssemblies=expected, harnessSourceCommit=manifest["sourceCommit"])
    return result


def deterministic_archive(directory, destination):
    """Exclude filesystem mtimes/permissions and publication-run IDs from immutable evidence."""
    files = sorted((p for p in directory.rglob("*") if p.is_file()),
                   key=lambda p: p.relative_to(directory).as_posix().encode("utf-8"))
    with ZipFile(destination, "w", compression=ZIP_DEFLATED, compresslevel=9) as archive:
        for file in files:
            info = ZipInfo(file.relative_to(directory).as_posix(), date_time=(1980, 1, 1, 0, 0, 0))
            info.create_system = 3
            info.external_attr = 0o100644 << 16
            archive.writestr(info, file.read_bytes(), compress_type=ZIP_DEFLATED, compresslevel=9)


def validate_assets(existing, expected, complete=False):
    names = [asset["name"] for asset in existing]
    require(len(names) == len(set(names)) and set(names).issubset(expected), "Release contains unexpected assets.")
    if complete:
        require(set(names) == set(expected), "Release assets are incomplete.")
    for asset in existing:
        require(asset["state"] == "uploaded" and
                all(asset.get(key) == value for key, value in expected[asset["name"]].items()),
                f"Existing or uploaded asset does not match: {asset['name']}")


def gh(*args):
    result = subprocess.run(["gh", *map(str, args)], capture_output=True, text=True)
    if result.returncode:
        error = re.sub(r"https?://\S+", lambda m: urllib.parse.urlunsplit(
            (*urllib.parse.urlsplit(m[0])[:3], "", "")), result.stderr)
        raise SystemExit(error.strip() or f"gh exited {result.returncode}")
    return result.stdout


def main():
    repository = os.environ["GITHUB_REPOSITORY"]
    version = os.environ["RELEASE_VERSION"]
    require(re.fullmatch(VERSION_PATTERN, version), "Invalid release version.")
    manifest_path = Path(".github/releases") / f"{version}.json"
    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    notes = manifest_path.with_suffix(".md").read_text(encoding="utf-8")
    policy = release_policy(manifest, version, notes)
    tag = manifest["tag"]
    source = manifest["sourceCommit"]
    require(re.fullmatch(r"[0-9a-f]{40}", source), "Invalid source commit.")
    api_root = f"repos/{repository}"

    def api(path):
        return json.loads(gh("api", f"{api_root}/{path}"))

    def validate_release(release):
        require(release["id"] == manifest["releaseId"] and
                release["prerelease"] is policy["prerelease"] and release["tag_name"] == tag and
                release["target_commitish"] == source, "Release policy or source mismatch.")
        require((release.get("body") or "").strip() == notes.strip(),
                "Release notes differ from the reviewed notes.")

    build_run = api(f"actions/runs/{manifest['buildRun']}")
    test_run = api(f"actions/runs/{manifest['fullTestRun']}")
    require(build_run["conclusion"] == "success" and build_run["head_sha"] == source and
            build_run["path"] == ".github/workflows/windows-build.yml" and
            build_run["run_attempt"] == manifest.get("buildAttempt", 1),
            "Package build did not pass for the configured source and attempt.")
    require(test_run["conclusion"] == "success" and
            test_run["head_sha"] == manifest["testAutomationCommit"] and
            test_run["path"] == ".github/workflows/full-software-tests.yml" and
            test_run["run_attempt"] == manifest.get("fullTestAttempt", 1),
            "Full test automation run did not match the reviewed successful run.")
    release = api(f"releases/{manifest['releaseId']}")
    validate_release(release)
    staging = Path("release-staging")
    evidence = staging / "evidence"
    assets = []

    def download(run, name, directory):
        directory.mkdir(parents=True, exist_ok=True)
        gh("run", "download", run, "--repo", repository, "--name", name, "--dir", directory)

    for flavor, expected in manifest["packages"].items():
        require(flavor in {"framework-dependent", "self-contained"}, "Unexpected package flavor.")
        destination = staging / flavor
        download(manifest["buildRun"], f"windows-preview-win-x64-{flavor}-{manifest.get('buildAttempt', 1)}", destination)
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
                    package_manifest["workflowRun"] == str(manifest["buildRun"]) and
                    package_manifest["workflowAttempt"] == str(manifest.get("buildAttempt", 1)) and
                    package_manifest["repository"] == repository and package_manifest["flavor"] == flavor and
                    package_manifest["runtimeIdentifier"] == "win-x64", "Package provenance mismatch.")
            require(package_manifest["applicationLaunched"] is False and package_manifest["hardwareVerified"] is False,
                    "Unexpected runtime/hardware verification claim.")
            names = archive.namelist()
            require(len(names) == len(set(names)) and
                    set(names) == set(package_manifest["files"]) | {"package-manifest.json"},
                    "Unexpected ZIP payload.")
            for name, expected_hash in package_manifest["files"].items():
                require(hashlib.sha256(archive.read(name)).hexdigest() == expected_hash,
                        f"Payload hash mismatch: {name}")
            if manifest.get("appSmokeMode") == "companion":
                require(all(package_manifest["files"].get(name) == expected_hash
                            for name, expected_hash in manifest["appSmokeAssemblyHashes"][flavor].items()),
                        "Companion-tested assemblies differ from the release package payload.")
        assets.extend([package, checksum])
    require(set(manifest["packages"]) == {"framework-dependent", "self-contained"}, "Both package flavors are required.")

    test_results = {}
    if "installer" in manifest:
        destination = staging / "installer"
        download(manifest["buildRun"], f"windows-installer-win-x64-{manifest.get('buildAttempt', 1)}", destination)
        installer = destination / f"DeviceBatteryStatus-{version}-win-x64-setup.exe"
        checksum = installer.with_suffix(".sha256")
        expected = manifest["installer"]
        digest = hashlib.sha256(installer.read_bytes()).hexdigest()
        require(installer.stat().st_size == expected["bytes"] and digest == expected["sha256"],
                "Installer bytes differ from the reviewed manifest.")
        require(checksum.read_text(encoding="utf-8-sig").strip() == f"{digest}  {installer.name}",
                "Installer checksum mismatch.")
        installer_metadata = single_json(destination, "installer-release.json")
        validate_installer_metadata(installer_metadata, manifest, repository)
        (evidence / "installer").mkdir(parents=True, exist_ok=True)
        (evidence / "installer" / "installer-release.json").write_text(
            json.dumps(installer_metadata, indent=2) + "\n", encoding="utf-8", newline="\n")
        smoke = evidence / "installer-smoke"
        download(manifest["buildRun"], f"installer-smoke-evidence-{manifest.get('buildAttempt', 1)}", smoke)
        test_results["installerSmoke"] = validate_installer_smoke(single_json(smoke, "installer-smoke.json"), manifest)
        assets.extend([installer, checksum])
    if manifest.get("appSmoke", False):
        test_results["applicationSmoke"] = {}
        for flavor in ["framework-dependent", "self-contained"]:
            destination = evidence / "application-smoke" / flavor
            download(manifest["buildRun"], f"application-smoke-evidence-{flavor}-{manifest.get('buildAttempt', 1)}", destination)
            test_results["applicationSmoke"][flavor] = validate_application_smoke(
                single_json(destination, "application-smoke.json"), manifest, repository, flavor)
    for configuration in ["Debug", "Release"]:
        destination = evidence / configuration
        download(manifest["fullTestRun"], f"full-software-tests-{configuration}-{manifest.get('fullTestAttempt', 1)}", destination)
        report = json.loads(next(destination.rglob(f"full-{configuration}.json")).read_text(encoding="utf-8-sig"))
        require(report["sourceCommit"] == source and report["configuration"] == configuration and
                report["nativeExit"] == 0, "Full test provenance mismatch.")
        expected_counts = {"total": manifest["testCount"], "executed": manifest["testCount"],
                           "passed": manifest["testCount"], "failed": 0, "notExecuted": 0}
        require(report["counters"] == expected_counts, "Full test counters do not match.")
        require(report["applicationLaunched"] is False and report["hardwareVerified"] is False,
                "Unexpected full test verification claim.")
        reports = list(destination.rglob("*.trx"))
        require(len(reports) == report["reportCount"] and len(reports) >= len(report["testProjects"]) > 0,
                "Missing test project reports.")
        counts = dict.fromkeys(expected_counts, 0)
        for trx in reports:
            counter = ET.parse(trx).getroot().find("{*}ResultSummary/{*}Counters")
            require(counter is not None, "Missing TRX counters.")
            for key in counts:
                counts[key] += int(counter.attrib[key])
        require(counts == expected_counts, "TRX counters do not match the summary.")
        test_results[configuration] = counts

    download(manifest["fullTestRun"], f"full-offline-parser-tests-{manifest.get('fullTestAttempt', 1)}", evidence / "offline")
    offline = next((evidence / "offline").rglob("*.log")).read_text(encoding="utf-8-sig")
    test_results["offlineParser"] = offline_test_counts(offline, manifest.get("parserTestCount", 5))
    if "automationTestCount" in manifest:
        download(manifest["fullTestRun"],
                 f"full-release-automation-tests-{manifest.get('fullTestAttempt', 1)}", evidence / "release-automation")
        automation_log = next((evidence / "release-automation").rglob("*.log")).read_text(encoding="utf-8-sig")
        test_results["releaseAutomation"] = offline_test_counts(automation_log, manifest["automationTestCount"])
    # This bundle is immutable evidence for the reviewed source/tests, not a publication-attempt log.
    product_launched = any(value.get("applicationLaunched") is True
                           for value in test_results.get("applicationSmoke", {}).values())
    harness_launched = any(value.get("harnessLaunched") is True
                           for value in test_results.get("applicationSmoke", {}).values())
    summary = {**manifest, "releasePolicy": policy, "testResults": test_results,
               "applicationLaunched": product_launched, "hardwareVerified": False}
    if harness_launched:
        summary["harnessLaunched"] = True
        summary["packagedApplicationAssembliesLoaded"] = True
    # Preserve old immutable verification bytes when these extensions are absent.
    if "installerSmoke" in test_results:
        summary["installerExecuted"] = True
    (evidence / "verification-summary.json").write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8", newline="\n")
    (evidence / "VERIFICATION.md").write_text(notes, encoding="utf-8", newline="\n")
    verification_dir = staging / "verification"
    verification_dir.mkdir(parents=True, exist_ok=True)
    verification = verification_dir / f"DeviceBatteryStatus-{version}-verification.zip"
    deterministic_archive(evidence, verification)
    verification_checksum = verification.with_suffix(".sha256")
    verification_checksum.write_text(f"{hashlib.sha256(verification.read_bytes()).hexdigest()}  {verification.name}\n",
                                     encoding="utf-8", newline="\n")
    assets.extend([verification, verification_checksum])
    expected_assets = {file.name: {"size": file.stat().st_size, "digest": "sha256:" + hashlib.sha256(file.read_bytes()).hexdigest()}
                       for file in assets}
    validate_assets(release["assets"], expected_assets, complete=not release["draft"])
    existing_names = {asset["name"] for asset in release["assets"]}
    missing = [file for file in assets if file.name not in existing_names]
    if missing:
        gh("release", "upload", tag, *missing, "--repo", repository)
    release = api(f"releases/{manifest['releaseId']}")
    validate_release(release)
    validate_assets(release["assets"], expected_assets, complete=True)
    # A missing tag is created at the draft's source; never repoint an existing tag.
    tags = json.loads(gh("api", "--paginate", "--slurp", f"{api_root}/git/matching-refs/tags/{tag}"))
    for page in tags:
        for ref in page:
            if ref["ref"] == f"refs/tags/{tag}":
                require(ref["object"]["type"] == "commit" and ref["object"]["sha"] == source,
                        "Existing release tag does not point to the verified source.")
    if release["draft"]:
        gh("release", "edit", tag, "--repo", repository, "--draft=false",
           f"--prerelease={str(policy['prerelease']).lower()}", f"--latest={str(policy['latest']).lower()}")
    published = api(f"releases/{manifest['releaseId']}")
    validate_release(published)
    validate_assets(published["assets"], expected_assets, complete=True)
    tag_ref = api(f"git/ref/tags/{tag}")
    require(not published["draft"] and published["published_at"] and
            tag_ref["object"]["type"] == "commit" and tag_ref["object"]["sha"] == source,
            "Published release or tag verification failed.")
    receipt = {"releaseUrl": published["html_url"], "releaseId": published["id"], "tag": tag,
               "sourceCommit": source, "publishedAt": published["published_at"], **policy,
               "fullTestRun": manifest["fullTestRun"], "testResults": test_results, "assets": expected_assets,
               "applicationLaunched": product_launched, "hardwareVerified": False,
               "installerExecuted": "installerSmoke" in test_results,
               "publicationWorkflowCommit": os.environ["GITHUB_SHA"], "publicationWorkflowRun": os.environ["GITHUB_RUN_ID"]}
    if harness_launched:
        receipt["harnessLaunched"] = True
        receipt["packagedApplicationAssembliesLoaded"] = True
    (staging / "publication-receipt.json").write_text(json.dumps(receipt, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(f"::notice title=Published verified release::{published['html_url']}; Source={source}; Assets={len(expected_assets)}; "
          f"Prerelease={str(policy['prerelease']).lower()}; Signed=false; Debug={manifest['testCount']} passed; "
          f"Release={manifest['testCount']} passed; Parser={manifest.get('parserTestCount', 5)} passed; "
          f"ApplicationLaunched={str(product_launched).lower()}; HarnessLaunched={str(harness_launched).lower()}; "
          f"InstallerExecuted={str('installerSmoke' in test_results).lower()}; HardwareVerified=false")


if __name__ == "__main__":
    main()
