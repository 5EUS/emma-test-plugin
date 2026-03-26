#!/usr/bin/env python3
import json
import pathlib
import re
import sys


def bump_patch(version: str) -> str:
    match = re.fullmatch(r"(\d+)\.(\d+)\.(\d+)", version.strip())
    if not match:
        raise ValueError(f"Unsupported version format '{version}'. Expected MAJOR.MINOR.PATCH")

    major, minor, patch = (int(part) for part in match.groups())
    return f"{major}.{minor}.{patch + 1}"


def main() -> int:
    repo_root = pathlib.Path(__file__).resolve().parent.parent
    manifest_path = repo_root / "EMMA.TestPlugin.plugin.json"

    if not manifest_path.exists():
        raise FileNotFoundError(f"Manifest not found: {manifest_path}")

    manifest = json.loads(manifest_path.read_text(encoding="utf-8"))
    current_version = str(manifest.get("version", "")).strip()
    if not current_version:
        raise ValueError("Manifest version is missing.")

    next_version = bump_patch(current_version)
    manifest["version"] = next_version
    manifest_path.write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")

    print(next_version)
    return 0


if __name__ == "__main__":
    sys.exit(main())
