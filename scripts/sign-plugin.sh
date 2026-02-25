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

if [[ -z "${EMMA_HMAC_KEY_BASE64:-}" ]]; then
  echo "Set EMMA_HMAC_KEY_BASE64 to the base64 HMAC key." >&2
  exit 1
fi

export MANIFEST_PATH

python3 - <<'PY'
import base64
import hashlib
import hmac
import json
import os
import sys

manifest_path = os.environ.get("MANIFEST_PATH")
key_b64 = os.environ.get("EMMA_HMAC_KEY_BASE64")

with open(manifest_path, "r", encoding="utf-8") as f:
    manifest = json.load(f)

payload = "|".join([
    manifest.get("id") or "",
    manifest.get("version") or "",
    manifest.get("protocol") or "",
])

key = base64.b64decode(key_b64)
digest = hmac.new(key, payload.encode("utf-8"), hashlib.sha256).digest()
signature_b64 = base64.b64encode(digest).decode("ascii")

signature = manifest.get("signature") or {}
signature["algorithm"] = "hmac-sha256"
signature["value"] = signature_b64
manifest["signature"] = signature

with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print("Updated signature for", manifest_path)
PY
