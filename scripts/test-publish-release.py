#!/usr/bin/env python3
"""Offline publisher boundary checks; no GitHub requests or releases are made."""

import argparse
import copy
from contextlib import redirect_stdout
import io
import hashlib
import importlib.util
import json
import os
from pathlib import Path
import tempfile
import unittest
from unittest.mock import patch

SPEC = importlib.util.spec_from_file_location("release_publisher", Path(__file__).with_name("publish-release.py"))
publisher = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(publisher)


class ReleasePublisherTests(unittest.TestCase):
    def setUp(self):
        self.directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.directory.cleanup)
        self.assets = Path(self.directory.name)
        self.source_directory = tempfile.TemporaryDirectory()
        self.addCleanup(self.source_directory.cleanup)
        self.source = Path(self.source_directory.name)
        root = patch.object(publisher, "ROOT", self.source)
        root.start()
        self.addCleanup(root.stop)
        self.version = "0.4.0"
        self.commit = "a" * 40
        for suffix in ("Setup.exe", "Portable.zip"):
            path = self.assets / f"ModernImageViewer-{self.version}-win-x64-{suffix}"
            path.write_bytes(f"fixture-{suffix}".encode())
            digest = hashlib.sha256(path.read_bytes()).hexdigest()
            Path(f"{path}.sha256").write_text(f"{digest}  {path.name}\n", encoding="utf-8")
        self.arguments = argparse.Namespace(version=self.version, commit=self.commit, prerelease="false", assets=self.assets)
        self.marker = f"<!-- ModernImageViewer-actions-release-v1 version={self.version} commit={self.commit} -->"
        self.release = None
        self.mutations = []
        self.uploads = 0
        environment = patch.dict(os.environ, {
            "GITHUB_EVENT_NAME": "workflow_dispatch", "GITHUB_REF": "refs/heads/master",
            "GITHUB_SHA": self.commit, "GITHUB_REPOSITORY": "owner/viewer", "GITHUB_STEP_SUMMARY": "",
        })
        environment.start()
        self.addCleanup(environment.stop)

    def run_command(self, *arguments, **kwargs):
        if arguments[:2] == ("git", "rev-parse"):
            return self.commit
        if arguments[:2] == ("gh", "api"):
            return json.dumps([[self.release] if self.release else []])
        if arguments[:3] == ("gh", "release", "upload"):
            self.uploads += 1
            self.release["assets"] = [
                {"id": number, "name": path.name, "size": path.stat().st_size, "state": "uploaded",
                 "digest": "sha256:" + hashlib.sha256(path.read_bytes()).hexdigest()}
                for number, path in enumerate(sorted(self.assets.iterdir()))
            ]
            return ""
        raise AssertionError(f"Unexpected command: {arguments}")

    def request(self, path, method="GET", data=None):
        if method != "GET":
            self.mutations.append((path, method, data))
        if "/compare/" in path:
            return {"status": "ahead"}
        if path.endswith("/generate-notes"):
            return {"body": "Changes"}
        if path.endswith("/git/refs"):
            return {}
        if path.endswith("/releases") and method == "POST":
            self.release = dict(data, id=1, assets=[], html_url="https://github.com/owner/viewer/releases/tag/v0.4.0")
            return copy.deepcopy(self.release)
        if "/releases/assets/" in path and method == "DELETE":
            asset_id = int(path.rsplit("/", 1)[1])
            self.release["assets"] = [item for item in self.release["assets"] if item["id"] != asset_id]
            return None
        if path.endswith("/releases/1"):
            if method == "PATCH":
                self.release.update(data)
            return copy.deepcopy(self.release)
        raise AssertionError(f"Unexpected API: {path} {method}")

    def publish(self, tag=None):
        with patch.object(publisher, "version_from_props", return_value=self.version), \
             patch.object(publisher, "run", side_effect=self.run_command), \
             patch.object(publisher, "api", side_effect=self.request), \
             patch.object(publisher, "tag_commit", side_effect=[tag, self.commit]), \
             redirect_stdout(io.StringIO()):
            publisher.publish(self.arguments)

    def test_non_master_release_request_is_rejected(self):
        with patch.dict(os.environ, {"GITHUB_REF": "refs/heads/feature"}):
            with self.assertRaisesRegex(ValueError, "master"):
                publisher.prepare()

    def test_tampered_binary_is_rejected_before_upload(self):
        next(self.assets.glob("*.exe")).write_bytes(b"tampered")
        with self.assertRaisesRegex(ValueError, "SHA256 mismatch"):
            self.publish()
        self.assertEqual(self.mutations, [])
        self.assertEqual(self.uploads, 0)

    def test_new_release_is_published_only_after_four_verified_assets(self):
        self.publish()
        self.assertFalse(self.release["draft"])
        self.assertEqual(len(self.release["assets"]), 4)
        self.assertEqual(self.mutations[1][2]["sha"], self.commit)
        self.assertEqual(self.mutations[-1][1], "PATCH")
        self.assertTrue(self.release["body"].endswith("Changes"))

    def test_checked_in_notes_are_included_with_generated_notes(self):
        notes = self.source / "docs" / "release-notes" / f"{self.version}.md"
        notes.parent.mkdir(parents=True)
        notes.write_text("# Release notes\n\nKnown limits: bounded animation frames.\n", encoding="utf-8")
        self.publish()
        self.assertIn("# Release notes\n\nKnown limits: bounded animation frames.\n\nChanges", self.release["body"])
        self.assertIn(self.marker, self.release["body"])

    def test_empty_checked_in_notes_are_rejected_before_mutation(self):
        notes = self.source / "docs" / "release-notes" / f"{self.version}.md"
        notes.parent.mkdir(parents=True)
        notes.write_text(" \n", encoding="utf-8")
        with self.assertRaisesRegex(ValueError, "must not be empty"):
            self.publish()
        self.assertEqual(self.mutations, [])
        self.assertEqual(self.uploads, 0)

    def test_partial_owned_draft_can_be_repaired(self):
        self.release = {"id": 1, "tag_name": "v0.4.0", "draft": True, "prerelease": False,
                        "body": self.marker, "html_url": "https://example.com/release", "assets": [
                            {"id": 2, "name": next(self.assets.glob("*.exe")).name}]}
        self.publish(tag=self.commit)
        self.assertFalse(self.release["draft"])
        self.assertEqual(len(self.release["assets"]), 4)
        self.assertEqual(self.mutations[0][1], "DELETE")

    def test_published_release_is_never_changed(self):
        self.release = {"id": 1, "tag_name": "v0.4.0", "draft": False, "prerelease": False, "body": self.marker}
        with self.assertRaisesRegex(ValueError, "already been published"):
            self.publish(tag=self.commit)
        self.assertEqual(self.mutations, [])
        self.assertEqual(self.uploads, 0)

    def test_unowned_draft_is_never_changed(self):
        self.release = {"id": 1, "tag_name": "v0.4.0", "draft": True, "prerelease": False, "body": "User draft"}
        with self.assertRaisesRegex(ValueError, "not created by this workflow"):
            self.publish(tag=self.commit)
        self.assertEqual(self.mutations, [])

    def test_existing_tag_is_never_moved(self):
        with self.assertRaisesRegex(ValueError, "different commit"):
            self.publish(tag="b" * 40)
        self.assertEqual(self.mutations, [])
        self.assertEqual(self.uploads, 0)


if __name__ == "__main__":
    unittest.main()
