#!/usr/bin/env python3
"""Publish only CI-verified, versioned assets; published releases are immutable."""

import argparse
import hashlib
import json
import os
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET

ROOT = Path(__file__).resolve().parent.parent
VERSION_PATTERN = r"(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)\.(?:0|[1-9]\d*)"


def version_from_props():
    versions = ET.parse(ROOT / "Directory.Build.props").findall(".//Version")
    if len(versions) != 1:
        raise ValueError("Directory.Build.props must define exactly one Version.")
    version = (versions[0].text or "").strip()
    if not re.fullmatch(VERSION_PATTERN, version):
        raise ValueError("Release Version must be major.minor.patch without leading zeros.")
    return version


def run(*arguments, input_text=None):
    result = subprocess.run(arguments, input=input_text, text=True, capture_output=True)
    if result.returncode:
        # gh diagnostics do not include its authorization header.
        raise RuntimeError(f"Command failed: {arguments[0]} {arguments[1]}: {result.stderr.strip()}")
    return result.stdout.strip()


def api(path, method="GET", data=None):
    arguments = ["gh", "api", path, "--method", method]
    if data is not None:
        arguments.extend(["--input", "-"])
    response = run(*arguments, input_text=json.dumps(data) if data is not None else None)
    return json.loads(response) if response else None


def prepare():
    if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch" or os.environ.get("GITHUB_REF") != "refs/heads/master":
        raise ValueError("Publishing is allowed only for a manual workflow run on master.")
    commit = os.environ.get("GITHUB_SHA", "")
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or run("git", "rev-parse", "HEAD") != commit:
        raise ValueError("Release checkout must exactly match the workflow commit.")
    version = version_from_props()
    with open(os.environ["GITHUB_OUTPUT"], "a", encoding="utf-8") as output:
        output.write(f"version={version}\ncommit={commit}\n")
    print(f"Validated release v{version} from master commit {commit}.")


def verified_assets(directory, version):
    directory = Path(directory)
    files = [directory / f"ModernImageViewer-{version}-win-x64-{kind}" for kind in ("Setup.exe", "Portable.zip")]
    expected_names = {item.name for binary in files for item in (binary, Path(f"{binary}.sha256"))}
    if not directory.is_dir() or {item.name for item in directory.iterdir()} != expected_names:
        raise ValueError("Release artifact must contain exactly the installer, portable ZIP and their two SHA256 files.")
    result = []
    for binary in files:
        if not binary.is_file() or binary.stat().st_size == 0:
            raise ValueError(f"Missing or empty release asset: {binary.name}")
        checksum = Path(f"{binary}.sha256")
        with binary.open("rb") as stream:
            digest = hashlib.file_digest(stream, "sha256").hexdigest()
        if checksum.read_text(encoding="utf-8").strip() != f"{digest}  {binary.name}":
            raise ValueError(f"SHA256 mismatch or incorrect filename: {binary.name}")
        result.extend((binary, checksum))
    return result


def tag_commit(repository, tag):
    # List the exact namespace so an absent tag is distinct from a network/API failure.
    refs = api(f"repos/{repository}/git/matching-refs/tags/{tag}")
    matches = [item for item in refs if item["ref"] == f"refs/tags/{tag}"]
    if not matches:
        return None
    object_info = matches[0]["object"]
    for _ in range(8):
        if object_info["type"] == "commit":
            return object_info["sha"]
        if object_info["type"] != "tag":
            break
        object_info = api(f"repos/{repository}/git/tags/{object_info['sha']}")["object"]
    raise ValueError("Release tag does not resolve to a commit.")


def releases(repository):
    # --slurp returns pages as arrays; pagination includes drafts visible to GH_TOKEN.
    pages = json.loads(run("gh", "api", f"repos/{repository}/releases?per_page=100", "--paginate", "--slurp"))
    return [release for page in pages for release in page]


def assert_recoverable(release, marker, prerelease):
    if not release["draft"]:
        raise ValueError("This version has already been published. Assets will not be overwritten; bump Version for a new release.")
    if marker not in (release.get("body") or "") or bool(release["prerelease"]) != prerelease:
        raise ValueError("Existing draft was not created by this workflow for the same commit and release type; refusing to alter it.")


def publish(arguments):
    if os.environ.get("GITHUB_EVENT_NAME") != "workflow_dispatch" or os.environ.get("GITHUB_REF") != "refs/heads/master":
        raise ValueError("Publishing is allowed only for a manual workflow run on master.")
    repository = os.environ.get("GITHUB_REPOSITORY", "")
    if not re.fullmatch(r"[\w.-]+/[\w.-]+", repository):
        raise ValueError("Invalid GITHUB_REPOSITORY.")
    if arguments.version != version_from_props():
        raise ValueError("Release version and the checked-out Directory.Build.props disagree.")
    commit = arguments.commit
    if not re.fullmatch(r"[0-9a-f]{40}", commit) or commit != os.environ.get("GITHUB_SHA") or run("git", "rev-parse", "HEAD") != commit:
        raise ValueError("Release checkout, requested commit and workflow commit must agree exactly.")
    prerelease = arguments.prerelease == "true"
    tag = f"v{arguments.version}"
    assets = verified_assets(arguments.assets, arguments.version)
    # A newer master commit is fine while a long-running build finishes; unrelated commits are not.
    comparison = api(f"repos/{repository}/compare/{commit}...master")
    if comparison["status"] not in ("ahead", "identical"):
        raise ValueError("The verified commit is no longer on master.")
    resolved_tag = tag_commit(repository, tag)
    if resolved_tag is not None and resolved_tag != commit:
        raise ValueError("Version tag already points to a different commit; it will not be moved.")
    matching_releases = [item for item in releases(repository) if item["tag_name"] == tag]
    if len(matching_releases) > 1:
        raise ValueError("Multiple releases use this version tag; resolve manually.")
    marker = f"<!-- ModernImageViewer-actions-release-v1 version={arguments.version} commit={commit} -->"
    release = matching_releases[0] if matching_releases else None
    if release is not None:
        assert_recoverable(release, marker, prerelease)
    else:
        notes = api(f"repos/{repository}/releases/generate-notes", "POST", {"tag_name": tag, "target_commitish": commit})
        if resolved_tag is None:
            api(f"repos/{repository}/git/refs", "POST", {"ref": f"refs/tags/{tag}", "sha": commit})
        body = (
            f"{marker}\n\nWindows x64 安装器与便携包；由已通过 CI 验证的提交 `{commit}` 构建。\n\n"
            "安装器为当前用户安装，应用自带 .NET 运行时；安装器暂未签名。"
            "两份 `.sha256` 文件用于核对下载文件的 SHA256。\n\n" + notes["body"]
        )
        release = api(f"repos/{repository}/releases", "POST", {
            "tag_name": tag, "target_commitish": commit, "name": f"ModernImageViewer {tag}",
            "body": body, "draft": True, "prerelease": prerelease,
        })
    release_path = f"repos/{repository}/releases/{release['id']}"
    # Only an owned draft may be repaired. Public releases never reach this code.
    current = api(release_path)
    assert_recoverable(current, marker, prerelease)
    names = {path.name for path in assets}
    if any(item["name"] not in names for item in current["assets"]):
        raise ValueError("Draft contains unexpected assets; refusing to remove or publish them.")
    for asset in current["assets"]:
        api(f"repos/{repository}/releases/assets/{asset['id']}", "DELETE")
    run("gh", "release", "upload", tag, *[str(path) for path in assets], "--repo", repository)
    current = api(release_path)
    assert_recoverable(current, marker, prerelease)
    uploaded = {item["name"]: item for item in current["assets"]}
    if set(uploaded) != names:
        raise ValueError("Uploaded release must have exactly four expected assets.")
    for path in assets:
        asset = uploaded[path.name]
        if asset["size"] != path.stat().st_size or asset["state"] != "uploaded":
            raise ValueError(f"Incomplete release asset: {path.name}")
        if asset.get("digest"):
            with path.open("rb") as stream:
                digest = "sha256:" + hashlib.file_digest(stream, "sha256").hexdigest()
            if asset["digest"] != digest:
                raise ValueError(f"Uploaded release SHA256 mismatch: {path.name}")
    if tag_commit(repository, tag) != commit:
        raise ValueError("Release tag changed during upload; draft will not be published.")
    published = api(release_path, "PATCH", {"draft": False, "make_latest": "false" if prerelease else "true"})
    print(f"Published {published['html_url']} from {commit} with four verified assets.")
    summary = os.environ.get("GITHUB_STEP_SUMMARY")
    if summary:
        with open(summary, "a", encoding="utf-8") as output:
            output.write(f"## Published GitHub Release\n\n[{tag}]({published['html_url']})\n\nCommit: `{commit}`\n")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("prepare")
    publishing = commands.add_parser("publish")
    publishing.add_argument("--version", required=True)
    publishing.add_argument("--commit", required=True)
    publishing.add_argument("--prerelease", choices=("true", "false"), required=True)
    publishing.add_argument("--assets", type=Path, required=True)
    arguments = parser.parse_args()
    try:
        prepare() if arguments.command == "prepare" else publish(arguments)
    except (ValueError, RuntimeError, OSError, ET.ParseError) as error:
        print(f"Release refused/failed: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    sys.exit(main())
