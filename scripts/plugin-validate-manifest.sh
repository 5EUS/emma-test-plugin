#!/usr/bin/env bash
set -euo pipefail

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <manifest-path>" >&2
  exit 1
fi

MANIFEST_PATH="$1"
if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

python3 - "$MANIFEST_PATH" <<'PY'
import json
import pathlib
import sys

manifest_path = pathlib.Path(sys.argv[1])
try:
    manifest = json.loads(manifest_path.read_text(encoding='utf-8'))
except json.JSONDecodeError as ex:
    print(f"Manifest validation failed: {manifest_path}")
    print(f" - Invalid JSON at line {ex.lineno}, column {ex.colno}: {ex.msg}")
    sys.exit(1)

errors = []

def required_str(name):
    value = manifest.get(name)
    if not isinstance(value, str) or not value.strip():
        errors.append(f"Missing or invalid required string field: {name}")

required_str("id")
required_str("name")
required_str("version")
required_str("protocol")

protocol = manifest.get("protocol")
if isinstance(protocol, str) and protocol.strip().lower() not in {"grpc"}:
    errors.append("Only 'grpc' protocol is currently supported for packaged plugins.")

signature = manifest.get("signature")
if signature is not None:
    if not isinstance(signature, dict):
        errors.append("signature must be an object when present.")
    else:
        algorithm = signature.get("algorithm")
        value = signature.get("value")
        if algorithm is not None and (not isinstance(algorithm, str) or not algorithm.strip()):
            errors.append("signature.algorithm must be a non-empty string when present.")
        if value is not None and (not isinstance(value, str) or not value.strip()):
            errors.append("signature.value must be a non-empty string when present.")

if errors:
    print(f"Manifest validation failed: {manifest_path}")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print(f"Manifest validation passed: {manifest_path}")
PY
