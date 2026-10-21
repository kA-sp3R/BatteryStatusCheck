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


class OfficialPublicationIntegrationTests(unittest.TestCase):
    def test_partial_upload_and_published_retry_preserve_assets_and_immutable_evidence(self):
        self.exercise_publication()

    def test_optional_release_automation_evidence_is_checked_and_bundled(self):
        self.exercise_publication(automation_count=7)

    def exercise_publication(self, automation_count=None):
        source, automation = "a" * 40, "b" * 40
        repository, notes = "owner/repo", "Unsigned official release. Runtime and hardware checks are deferred.\n"
        manifest = {"version": "1.0.0", "tag": "v1.0.0", "releaseId": 101, "sourceCommit": source,
                    "buildRun": 11, "fullTestRun": 22, "testAutomationCommit": automation,
                    "testCount": 2, "parserTestCount": 3, "prerelease": False, "signed": False, "packages": {}}
        fixtures = {}
        for flavor in ["framework-dependent", "self-contained"]:
            package_manifest = {"sourceCommit": source, "version": "1.0.0", "workflowRun": "11", "workflowAttempt": "1",
                                "repository": repository, "flavor": flavor, "runtimeIdentifier": "win-x64",
                                "applicationLaunched": False, "hardwareVerified": False, "files": {}}
            buffer = io.BytesIO()
            with ZipFile(buffer, "w") as archive:
                archive.writestr("package-manifest.json", json.dumps(package_manifest))
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
                    publisher.main()
                    first_digest = next(asset["digest"] for asset in release["assets"] if asset["name"].endswith("-verification.zip"))
                    self.assertEqual(6, len(release["assets"]))
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
