"""Hardware-free publication policy tests; all GitHub operations in the integration case are fake."""
import contextlib
import copy
import hashlib
import importlib.util
import io
import json
import os
from pathlib import Path
import shutil
import tempfile
import unittest
from unittest.mock import patch
from zipfile import ZipFile

spec = importlib.util.spec_from_file_location("publisher", Path(__file__).with_name("publish-verified-preview.py"))
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class ReleasePolicyTests(unittest.TestCase):
    def stable(self):
        return {"version": "1.0.0", "tag": "v1.0.0", "prerelease": False,
                "signed": False, "testCount": 2, "parserTestCount": 3}

    def test_original_preview_manifest_defaults_are_preserved(self):
        manifest = {"version": "0.1.0-preview.3", "tag": "v0.1.0-preview.3", "testCount": 451}
        self.assertEqual({"prerelease": True, "latest": False, "signed": False},
                         publisher.release_policy(manifest, manifest["version"], "Unsigned preview"))

    def test_stable_requires_explicit_policy_and_defaults_to_latest(self):
        self.assertEqual({"prerelease": False, "latest": True, "signed": False},
                         publisher.release_policy(self.stable(), "1.0.0", "Unsigned release"))
        for field in ["prerelease", "signed"]:
            manifest = self.stable()
            del manifest[field]
            with self.subTest(field=field), self.assertRaises(SystemExit):
                publisher.release_policy(manifest, "1.0.0", "Unsigned release")

    def test_stable_can_deliberately_avoid_latest(self):
        manifest = {**self.stable(), "latest": False}
        self.assertFalse(publisher.release_policy(manifest, "1.0.0", "Unsigned release")["latest"])

    def test_policy_rejects_signing_claims_or_missing_unsigned_disclosure(self):
        for manifest, notes in [({**self.stable(), "signed": True}, "Unsigned release"),
                                (self.stable(), "Official release")]:
            with self.subTest(notes=notes), self.assertRaises(SystemExit):
                publisher.release_policy(manifest, "1.0.0", notes)

    def test_preview_cannot_be_published_as_stable_or_latest(self):
        for extra in [{"prerelease": False}, {"latest": True}, {"prerelease": "true"}]:
            manifest = {"version": "1.0.0-preview.1", "tag": "v1.0.0-preview.1", "testCount": 2, **extra}
            with self.subTest(policy=extra), self.assertRaises(SystemExit):
                publisher.release_policy(manifest, manifest["version"], "Unsigned preview")

    def test_invalid_versions_tags_and_counts_are_rejected(self):
        for version in ["../1.0.0", "01.0.0", "1.0", "1.0.0-preview.-1"]:
            with self.subTest(version=version), self.assertRaises(SystemExit):
                publisher.release_policy(self.stable(), version, "Unsigned release")
        for field in ["testCount", "parserTestCount", "buildAttempt", "fullTestAttempt", "automationTestCount"]:
            for value in [0, -1, True, "2"]:
                with self.subTest(field=field, value=value), self.assertRaises(SystemExit):
                    publisher.release_policy({**self.stable(), field: value}, "1.0.0", "Unsigned release")
        with self.assertRaises(SystemExit):
            publisher.release_policy({**self.stable(), "tag": "v2.0.0"}, "1.0.0", "Unsigned release")

    def test_optional_smoke_and_installer_policies_reject_ambiguous_values(self):
        for extra in [{"appSmoke": "true"}, {"appSmoke": 1}, {"appSmokeCheckCount": 0},
                      {"appSmokeCheckCount": True}, {"installer": True}, {"installer": {}},
                      {"installer": {"bytes": True, "sha256": "a" * 64}},
                      {"installer": {"bytes": 42, "sha256": "A" * 64}}]:
            with self.subTest(extra=extra), self.assertRaises(SystemExit):
                publisher.release_policy({**self.stable(), **extra}, "1.0.0", "Unsigned release")
        publisher.release_policy({**self.stable(), "appSmoke": True, "appSmokeCheckCount": 8,
                                  "installer": {"bytes": 42, "sha256": "a" * 64}}, "1.0.0", "Unsigned release")

    def test_offline_result_count_is_configurable_and_exact(self):
        self.assertEqual({"executed": 3, "passed": 3, "failed": 0, "skipped": 0},
                         publisher.offline_test_counts("Ran 3 tests in 0.01s\n\nOK\n\nnative_exit_code=0\n", 3))
        for log in ["Ran 30 tests\nOK\nnative_exit_code=0\n", "Ran 3 tests\nOK (skipped=1)\nnative_exit_code=0\n",
                    "Ran 3 tests\nFAILED\nOK\nnative_exit_code=0\n", "Ran 3 tests\nOK\nnative_exit_code=1\n"]:
            with self.subTest(log=log), self.assertRaises(SystemExit):
                publisher.offline_test_counts(log, 3)

    def test_archive_bytes_ignore_filesystem_time_permissions_and_creation_order(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            first, second = root / "first", root / "second"
            first.mkdir()
            second.mkdir()
            for folder, names, timestamp in [(first, ["b.txt", "a.txt"], 1000000000),
                                             (second, ["a.txt", "b.txt"], 1500000000)]:
                for name in names:
                    file = folder / name
                    file.write_text(name)
                    os.utime(file, (timestamp, timestamp))
            publisher.deterministic_archive(first, root / "first.zip")
            publisher.deterministic_archive(second, root / "second.zip")
            self.assertEqual((root / "first.zip").read_bytes(), (root / "second.zip").read_bytes())
            with ZipFile(root / "first.zip") as archive:
                self.assertEqual(["a.txt", "b.txt"], archive.namelist())
                self.assertTrue(all(item.date_time == (1980, 1, 1, 0, 0, 0) for item in archive.infolist()))

    def test_partial_assets_are_accepted_only_when_every_existing_byte_matches(self):
        expected = {"a": {"size": 2, "digest": "sha256:aa"}, "b": {"size": 3, "digest": "sha256:bb"}}
        matching = {"name": "a", "state": "uploaded", **expected["a"]}
        publisher.validate_assets([matching], expected)
        with self.assertRaises(SystemExit):
            publisher.validate_assets([matching], expected, complete=True)
        for asset in [{**matching, "digest": "sha256:wrong"}, {**matching, "state": "starter"},
                      {**matching, "name": "unexpected"}]:
            with self.subTest(asset=asset), self.assertRaises(SystemExit):
                publisher.validate_assets([asset], expected)
        with self.assertRaises(SystemExit):
            publisher.validate_assets([matching, matching], expected)


class SmokeEvidenceValidationTests(unittest.TestCase):
    def manifest(self):
        return {"version": "1.0.0", "sourceCommit": "a" * 40, "buildRun": 11,
                "installer": {"bytes": 42, "sha256": "b" * 64},
                "packages": {"self-contained": {"bytes": 84, "sha256": "c" * 64}}}

    def installer_metadata(self):
        manifest = self.manifest()
        return {"schemaVersion": 1, "product": "Device Battery Status", "version": "1.0.0", "runtimeIdentifier": "win-x64",
                "distribution": "per-user-installer", "signed": False, "sourceCommit": "a" * 40, "repository": "owner/repo",
                "workflowRun": "11", "workflowAttempt": "1", "applicationLaunched": False, "hardwareVerified": False,
                "appId": "D38FB615-37DB-4AC0-92CE-BD1F947B9732",
                "package": {"name": "DeviceBatteryStatus-1.0.0-win-x64-self-contained.zip", "bytes": 84, "sha256": "c" * 64,
                            "workflowRun": "11", "workflowAttempt": "1"},
                "installer": {"name": "DeviceBatteryStatus-1.0.0-win-x64-setup.exe", **manifest["installer"]},
                "compiler": {"product": "Inno Setup", "version": "6.7.1", "sha256": "d" * 64}}

    def installer_smoke(self):
        return {"schemaVersion": 1, "passed": True, "sourceCommit": "a" * 40, "version": "1.0.0", "workflowRun": "11",
                "workflowAttempt": "1", "installerSha256": "b" * 64, "installerBytes": 42,
                "checks": ["Initial install", "Upgrade", "Settings preserved", "Clean uninstall"], "checkCount": 4,
                "initialInstallPassed": True, "upgradePassed": True, "ownedStartupUninstallPassed": True,
                "foreignStartupUninstallPassed": True, "settingsPreserved": True, "cleanUninstall": True,
                "installerExecuted": True, "applicationLaunched": False, "hardwareVerified": False}

    def application_smoke(self):
        return {"schemaVersion": 1, "mode": "hardware-free-windows-application-smoke", "passed": True,
                "appVersion": "1.0.0", "sourceCommit": "a" * 40, "repository": "owner/repo", "workflowRun": "11",
                "workflowAttempt": "1", "flavor": "self-contained", "runtimeIdentifier": "win-x64",
                "packagePayloadVerified": True, "counters": {"total": 8, "executed": 8, "passed": 8, "failed": 0},
                "checks": [{"name": f"check-{index}", "passed": True} for index in range(8)],
                "applicationLaunched": True, "normalApplicationControllerStarted": False, "hardwareAccessed": False,
                "networkAccessed": False, "startupRegistrationAccessed": False, "userSettingsPersisted": False}

    def test_installer_source_payload_and_unsigned_provenance_must_match(self):
        manifest, report = self.manifest(), self.installer_metadata()
        publisher.validate_installer_metadata(report, manifest, "owner/repo")
        for field, bad in [("sourceCommit", "e" * 40), ("workflowAttempt", "2"), ("signed", True),
                           ("applicationLaunched", True), ("appId", "different")]:
            with self.subTest(field=field), self.assertRaises(SystemExit):
                publisher.validate_installer_metadata({**report, field: bad}, manifest, "owner/repo")
        for field in ["package", "installer", "compiler"]:
            altered = copy.deepcopy(report)
            altered[field]["sha256"] = "wrong"
            with self.subTest(field=field), self.assertRaises(SystemExit):
                publisher.validate_installer_metadata(altered, manifest, "owner/repo")

    def test_installer_smoke_requires_each_lifecycle_result_and_exact_installer(self):
        manifest, report = self.manifest(), self.installer_smoke()
        self.assertTrue(publisher.validate_installer_smoke(report, manifest)["installerExecuted"])
        for field in ["initialInstallPassed", "upgradePassed", "ownedStartupUninstallPassed", "foreignStartupUninstallPassed",
                      "settingsPreserved", "cleanUninstall", "installerExecuted"]:
            with self.subTest(field=field), self.assertRaises(SystemExit):
                publisher.validate_installer_smoke({**report, field: False}, manifest)
        for extra in [{"checkCount": 5}, {"installerSha256": "e" * 64}, {"workflowRun": "12"},
                      {"applicationLaunched": True}, {"hardwareVerified": True}, {"checks": ["same"] * 4}]:
            with self.subTest(extra=extra), self.assertRaises(SystemExit):
                publisher.validate_installer_smoke({**report, **extra}, manifest)

    def test_application_smoke_requires_real_passed_checks_and_explicit_isolation(self):
        manifest, report = self.manifest(), self.application_smoke()
        self.assertTrue(publisher.validate_application_smoke(report, manifest, "owner/repo", "self-contained")["applicationLaunched"])
        for extra in [{"sourceCommit": "e" * 40}, {"flavor": "framework-dependent"}, {"packagePayloadVerified": False},
                      {"applicationLaunched": False}, {"normalApplicationControllerStarted": True},
                      {"hardwareAccessed": True}, {"networkAccessed": True}, {"startupRegistrationAccessed": True},
                      {"userSettingsPersisted": True}, {"counters": {"total": 8, "executed": 8, "passed": 7, "failed": 1}},
                      {"checks": [{"name": "same", "passed": True}] * 8},
                      {"checks": [{"name": f"check-{i}", "passed": i != 3} for i in range(8)]}]:
            with self.subTest(extra=extra), self.assertRaises(SystemExit):
                publisher.validate_application_smoke({**report, **extra}, manifest, "owner/repo", "self-contained")

    def companion_hashes(self):
        return {name: str(index) * 64 for index, name in enumerate(
            ["DeviceBatteryStatus.dll", "DeviceBatteryStatus.Core.dll", "DeviceBatteryStatus.Windows.dll"], 1)}

    def test_companion_requires_exact_packaged_assemblies_and_honest_launch_scope(self):
        hashes = self.companion_hashes()
        manifest = {**self.manifest(), "appSmoke": True, "appSmokeMode": "companion",
                    "appSmokeAssemblyHashes": {flavor: hashes for flavor in ["framework-dependent", "self-contained"]}}
        report = {**self.application_smoke(), "schemaVersion": 2, "mode": "hardware-free-windows-companion-smoke",
                  "applicationLaunched": False, "harnessLaunched": True, "packagedApplicationAssembliesLoaded": True,
                  "harnessExecuted": True, "separateHarnessVerified": True,
                  "harnessSourceCommit": manifest["sourceCommit"], "loadedApplicationAssemblies": hashes}
        result = publisher.validate_application_smoke(report, manifest, "owner/repo", "self-contained")
        self.assertFalse(result["applicationLaunched"])
        self.assertTrue(result["harnessLaunched"])
        self.assertEqual(hashes, result["loadedApplicationAssemblies"])
        for extra in [{"schemaVersion": 1}, {"mode": "hardware-free-windows-application-smoke"},
                      {"applicationLaunched": True}, {"harnessLaunched": False},
                      {"harnessExecuted": False}, {"separateHarnessVerified": False},
                      {"packagedApplicationAssembliesLoaded": False}, {"harnessSourceCommit": "e" * 40},
                      {"loadedApplicationAssemblies": {}},
                      {"loadedApplicationAssemblies": {**hashes, "DeviceBatteryStatus.dll": "f" * 64}}]:
            with self.subTest(extra=extra), self.assertRaises(SystemExit):
                publisher.validate_application_smoke({**report, **extra}, manifest, "owner/repo", "self-contained")
        with self.assertRaises(SystemExit):
            publisher.validate_application_smoke(self.application_smoke(), manifest, "owner/repo", "self-contained")

    def test_release_policy_requires_complete_companion_hashes(self):
        hashes = {flavor: self.companion_hashes() for flavor in ["framework-dependent", "self-contained"]}
        manifest = {**self.manifest(), "tag": "v1.0.0", "prerelease": False, "signed": False, "testCount": 8,
                    "appSmoke": True, "appSmokeMode": "companion", "appSmokeAssemblyHashes": hashes}
        publisher.release_policy(manifest, "1.0.0", "Unsigned release.")
        for extra in [{"appSmoke": False}, {"appSmokeMode": "other"}, {"appSmokeAssemblyHashes": {}},
                      {"appSmokeAssemblyHashes": {"self-contained": self.companion_hashes()}}]:
            with self.subTest(extra=extra), self.assertRaises(SystemExit):
                publisher.release_policy({**manifest, **extra}, "1.0.0", "Unsigned release.")


class OfficialPublicationIntegrationTests(unittest.TestCase):
    def test_partial_upload_and_published_retry_preserve_assets_and_immutable_evidence(self):
        self.exercise_publication()

    def test_optional_release_automation_evidence_is_checked_and_bundled(self):
        self.exercise_publication(automation_count=7)

    def test_optional_installer_and_application_runtime_evidence_are_checked_and_bundled(self):
        self.exercise_publication(with_installer=True, with_app_smoke=True)

    def test_failed_installer_lifecycle_cannot_upload_or_publish(self):
        self.exercise_publication(with_installer=True, corrupt_optional="installer")

    def test_failed_application_smoke_cannot_upload_or_publish(self):
        self.exercise_publication(with_app_smoke=True, corrupt_optional="application")

    def test_companion_verification_preserves_payload_hashes_and_honest_receipt(self):
        self.exercise_publication(with_app_smoke=True, companion_smoke=True)

    def test_companion_package_mismatch_cannot_upload_or_publish(self):
        self.exercise_publication(with_app_smoke=True, companion_smoke=True, corrupt_optional="companion-package")

    def exercise_publication(self, automation_count=None, with_installer=False, with_app_smoke=False, corrupt_optional=None,
                             companion_smoke=False):
        source, automation = "a" * 40, "b" * 40
        repository, notes = "owner/repo", "Unsigned official release. Runtime and hardware checks are deferred.\n"
        manifest = {"version": "1.0.0", "tag": "v1.0.0", "releaseId": 101, "sourceCommit": source,
                    "buildRun": 11, "fullTestRun": 22, "testAutomationCommit": automation,
                    "testCount": 2, "parserTestCount": 3, "prerelease": False, "signed": False, "packages": {}}
        fixtures = {}
        companion_package_hashes = {}
        for flavor in ["framework-dependent", "self-contained"]:
            package_manifest = {"sourceCommit": source, "version": "1.0.0", "workflowRun": "11", "workflowAttempt": "1",
                                "repository": repository, "flavor": flavor, "runtimeIdentifier": "win-x64",
                                "applicationLaunched": False, "hardwareVerified": False, "files": {}}
            payload = {}
            if companion_smoke:
                payload = {name: ("Synthetic assembly fixture: " + name).encode() for name in
                           ["DeviceBatteryStatus.dll", "DeviceBatteryStatus.Core.dll", "DeviceBatteryStatus.Windows.dll"]}
                package_manifest["files"] = {name: hashlib.sha256(data).hexdigest() for name, data in payload.items()}
                companion_package_hashes[flavor] = package_manifest["files"]
            buffer = io.BytesIO()
            with ZipFile(buffer, "w") as archive:
                archive.writestr("package-manifest.json", json.dumps(package_manifest))
                for name, data in payload.items():
                    archive.writestr(name, data)
            name = f"DeviceBatteryStatus-1.0.0-win-x64-{flavor}.zip"
            data = buffer.getvalue()
            digest = hashlib.sha256(data).hexdigest()
            fixtures[f"windows-preview-win-x64-{flavor}-1"] = {
                name: data, name.removesuffix(".zip") + ".sha256": f"{digest}  {name}\n".encode()}
            manifest["packages"][flavor] = {"bytes": len(data), "sha256": digest}
        counts = {"total": 2, "executed": 2, "passed": 2, "failed": 0, "notExecuted": 0}
        for configuration in ["Debug", "Release"]:
            report = {"sourceCommit": source, "configuration": configuration, "nativeExit": 0, "counters": counts,
                      "applicationLaunched": False, "hardwareVerified": False, "reportCount": 1, "testProjects": ["tests/Test.csproj"]}
            trx = '<TestRun><ResultSummary><Counters total="2" executed="2" passed="2" failed="0" notExecuted="0"/></ResultSummary></TestRun>'
            fixtures[f"full-software-tests-{configuration}-1"] = {
                f"TestResults/full-{configuration}.json": json.dumps(report).encode(), "TestResults/results.trx": trx.encode()}
        fixtures["full-offline-parser-tests-1"] = {"ci-logs/offline.log": b"Ran 3 tests in 0.01s\n\nOK\n\nnative_exit_code=0\n"}
        if automation_count is not None:
            manifest["automationTestCount"] = automation_count
            fixtures["full-release-automation-tests-1"] = {
                "ci-logs/release-automation.log":
                    f"Ran {automation_count} tests in 0.01s\n\nOK\n\nnative_exit_code=0\n".encode()}
        helper = SmokeEvidenceValidationTests()
        if with_installer:
            data = b"MZ synthetic installer fixture; never executed"
            digest = hashlib.sha256(data).hexdigest()
            name = "DeviceBatteryStatus-1.0.0-win-x64-setup.exe"
            manifest["installer"] = {"bytes": len(data), "sha256": digest}
            metadata = helper.installer_metadata()
            metadata["package"] = {"name": "DeviceBatteryStatus-1.0.0-win-x64-self-contained.zip",
                                   **manifest["packages"]["self-contained"], "workflowRun": "11", "workflowAttempt": "1"}
            metadata["installer"] = {"name": name, **manifest["installer"]}
            fixtures["windows-installer-win-x64-1"] = {name: data, name.removesuffix(".exe") + ".sha256": f"{digest}  {name}\n".encode(),
                                                        "installer-release.json": json.dumps(metadata).encode()}
            smoke = {**helper.installer_smoke(), "installerSha256": digest, "installerBytes": len(data)}
            if corrupt_optional == "installer":
                smoke["foreignStartupUninstallPassed"] = False
            fixtures["installer-smoke-evidence-1"] = {"installer-smoke.json": json.dumps(smoke).encode(), "install.log": b"Synthetic fixture"}
        if with_app_smoke:
            manifest["appSmoke"] = True
            if companion_smoke:
                manifest["appSmokeMode"] = "companion"
                manifest["appSmokeAssemblyHashes"] = copy.deepcopy(companion_package_hashes)
                if corrupt_optional == "companion-package":
                    manifest["appSmokeAssemblyHashes"]["self-contained"]["DeviceBatteryStatus.dll"] = "f" * 64
            for flavor in ["framework-dependent", "self-contained"]:
                smoke = {**helper.application_smoke(), "flavor": flavor}
                if companion_smoke:
                    smoke.update(schemaVersion=2, mode="hardware-free-windows-companion-smoke", applicationLaunched=False,
                                 harnessLaunched=True, harnessExecuted=True, separateHarnessVerified=True,
                                 packagedApplicationAssembliesLoaded=True, harnessSourceCommit=source,
                                 loadedApplicationAssemblies=manifest["appSmokeAssemblyHashes"][flavor])
                if corrupt_optional == "application":
                    smoke["checks"][0]["passed"] = False
                fixtures[f"application-smoke-evidence-{flavor}-1"] = {"application-smoke.json": json.dumps(smoke).encode()}
        release = {"id": 101, "tag_name": "v1.0.0", "target_commitish": source, "prerelease": False,
                   "draft": True, "body": notes, "assets": [], "html_url": "https://github.com/owner/repo/releases/tag/v1.0.0"}
        # Start from an interrupted attempt with one verified application asset already present.
        partial_name, partial_data = next(iter(fixtures["windows-preview-win-x64-framework-dependent-1"].items()))
        release["assets"].append({"name": partial_name, "state": "uploaded", "size": len(partial_data),
                                  "digest": "sha256:" + hashlib.sha256(partial_data).hexdigest()})
        mutations = []

        def fake_gh(*args):
            args = [str(arg) for arg in args]
            if args[:2] == ["run", "download"]:
                destination = Path(args[args.index("--dir") + 1])
                for name, data in fixtures[args[args.index("--name") + 1]].items():
                    file = destination / name
                    file.parent.mkdir(parents=True, exist_ok=True)
                    file.write_bytes(data)
                return ""
            if args[:2] == ["release", "upload"]:
                mutations.append(tuple(args[:2]))
                for name in args[3:args.index("--repo")]:
                    file = Path(name)
                    data = file.read_bytes()
                    self.assertNotIn(file.name, [asset["name"] for asset in release["assets"]])
                    release["assets"].append({"name": file.name, "state": "uploaded", "size": len(data),
                                              "digest": "sha256:" + hashlib.sha256(data).hexdigest()})
                return ""
            if args[:2] == ["release", "edit"]:
                self.assertIn("--prerelease=false", args)
                self.assertIn("--latest=true", args)
                mutations.append(tuple(args[:2]))
                release["draft"] = False
                release["published_at"] = "2026-10-21T00:00:00Z"
                return ""
            self.assertEqual("api", args[0])
            path = args[-1]
            if path.endswith("actions/runs/11"):
                return json.dumps({"conclusion": "success", "head_sha": source, "path": ".github/workflows/windows-build.yml", "run_attempt": 1})
            if path.endswith("actions/runs/22"):
                return json.dumps({"conclusion": "success", "head_sha": automation, "path": ".github/workflows/full-software-tests.yml", "run_attempt": 1})
            if path.endswith("releases/101"):
                return json.dumps(copy.deepcopy(release))
            if "matching-refs" in path:
                return "[[]]"
            if path.endswith("git/ref/tags/v1.0.0"):
                return json.dumps({"object": {"type": "commit", "sha": source}})
            self.fail(f"Unexpected fake GitHub request: {args}")

        with tempfile.TemporaryDirectory() as temporary:
            previous = Path.cwd()
            os.chdir(temporary)
            try:
                directory = Path(".github/releases")
                directory.mkdir(parents=True)
                (directory / "1.0.0.json").write_text(json.dumps(manifest))
                (directory / "1.0.0.md").write_text(notes)
                env = {"GITHUB_REPOSITORY": repository, "RELEASE_VERSION": "1.0.0", "GITHUB_SHA": "c" * 40, "GITHUB_RUN_ID": "33"}
                with patch.dict(os.environ, env), patch.object(publisher, "gh", side_effect=fake_gh), contextlib.redirect_stdout(io.StringIO()):
                    if corrupt_optional is not None:
                        with self.assertRaises(SystemExit):
                            publisher.main()
                        self.assertEqual([], mutations)
                        self.assertTrue(release["draft"])
                        return
                    publisher.main()
                    first_digest = next(asset["digest"] for asset in release["assets"] if asset["name"].endswith("-verification.zip"))
                    self.assertEqual(8 if with_installer else 6, len(release["assets"]))
                    self.assertEqual([("release", "upload"), ("release", "edit")], mutations)
                    shutil.rmtree("release-staging")
                    os.environ["GITHUB_RUN_ID"] = "34"
                    os.environ["GITHUB_SHA"] = "d" * 40
                    publisher.main()
                    self.assertEqual([("release", "upload"), ("release", "edit")], mutations)
                    receipt = json.loads(Path("release-staging/publication-receipt.json").read_text())
                    self.assertFalse(receipt["prerelease"])
                    self.assertFalse(receipt["signed"])
                    self.assertEqual("34", receipt["publicationWorkflowRun"])
                    self.assertEqual(first_digest, receipt["assets"]["DeviceBatteryStatus-1.0.0-verification.zip"]["digest"])
                    self.assertEqual(with_app_smoke and not companion_smoke, receipt["applicationLaunched"])
                    if companion_smoke:
                        self.assertTrue(receipt["harnessLaunched"])
                        self.assertTrue(receipt["packagedApplicationAssembliesLoaded"])
                    self.assertFalse(receipt["hardwareVerified"])
                    self.assertEqual(with_installer, receipt["installerExecuted"])
                    with ZipFile("release-staging/verification/DeviceBatteryStatus-1.0.0-verification.zip") as archive:
                        summary = json.loads(archive.read("verification-summary.json"))
                        self.assertEqual(with_app_smoke and not companion_smoke, summary["applicationLaunched"])
                        if companion_smoke:
                            self.assertTrue(summary["harnessLaunched"])
                        if not with_installer:
                            self.assertNotIn("installerExecuted", summary)
                        if not with_installer and not with_app_smoke:
                            self.assertEqual({**manifest, "releasePolicy": {"prerelease": False, "latest": True, "signed": False},
                                              "testResults": receipt["testResults"], "applicationLaunched": False, "hardwareVerified": False}, summary)
                        if with_installer:
                            self.assertIn("installer/installer-release.json", archive.namelist())
                            self.assertIn("installer-smoke/installer-smoke.json", archive.namelist())
                            self.assertTrue(receipt["testResults"]["installerSmoke"]["installerExecuted"])
                        if with_app_smoke:
                            self.assertIn("application-smoke/self-contained/application-smoke.json", archive.namelist())
                            self.assertIn("application-smoke/framework-dependent/application-smoke.json", archive.namelist())
                            self.assertEqual(not companion_smoke,
                                receipt["testResults"]["applicationSmoke"]["self-contained"]["applicationLaunched"])
                    if automation_count is None:
                        self.assertNotIn("releaseAutomation", receipt["testResults"])
                    else:
                        self.assertEqual({"executed": automation_count, "passed": automation_count, "failed": 0, "skipped": 0},
                                         receipt["testResults"]["releaseAutomation"])
                        with ZipFile("release-staging/verification/DeviceBatteryStatus-1.0.0-verification.zip") as archive:
                            self.assertIn("release-automation/ci-logs/release-automation.log", archive.namelist())
            finally:
                os.chdir(previous)


if __name__ == "__main__":
    unittest.main()
