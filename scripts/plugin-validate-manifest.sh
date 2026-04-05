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
import re
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
        key_id = signature.get("keyId")
        repository_id = signature.get("repositoryId")
        issued_at_utc = signature.get("issuedAtUtc")
        expires_at_utc = signature.get("expiresAtUtc")
        manifest_digest = signature.get("manifestDigestSha256")
        payload_digest = signature.get("payloadDigestSha256")

        if algorithm is not None:
            if not isinstance(algorithm, str) or not algorithm.strip():
                errors.append("signature.algorithm must be a non-empty string when present.")
            elif algorithm.strip().lower() != "rsa-sha256":
                errors.append("signature.algorithm must be 'rsa-sha256' when signature is present.")

        if value is not None and (not isinstance(value, str) or not value.strip()):
            errors.append("signature.value must be a non-empty string when present.")

        if key_id is not None and (not isinstance(key_id, str) or not key_id.strip()):
            errors.append("signature.keyId must be a non-empty string when present.")

        if repository_id is not None and (not isinstance(repository_id, str) or not repository_id.strip()):
            errors.append("signature.repositoryId must be a non-empty string when present.")

        if issued_at_utc is not None and (not isinstance(issued_at_utc, str) or not issued_at_utc.strip()):
            errors.append("signature.issuedAtUtc must be a non-empty string when present.")

        if expires_at_utc is not None and (not isinstance(expires_at_utc, str) or not expires_at_utc.strip()):
            errors.append("signature.expiresAtUtc must be a non-empty string when present.")

        if manifest_digest is not None:
            if not isinstance(manifest_digest, str) or not re.fullmatch(r"[0-9a-fA-F]{64}", manifest_digest):
                errors.append("signature.manifestDigestSha256 must be a 64-char hex SHA-256 when present.")

        if payload_digest is not None:
            if not isinstance(payload_digest, str) or (payload_digest and not re.fullmatch(r"[0-9a-fA-F]{64}", payload_digest)):
                errors.append("signature.payloadDigestSha256 must be empty or a 64-char hex SHA-256 when present.")

if errors:
    print(f"Manifest validation failed: {manifest_path}")
    for error in errors:
        print(f" - {error}")
    sys.exit(1)

print(f"Manifest validation passed: {manifest_path}")
PY
