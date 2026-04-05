#!/usr/bin/env bash
set -euo pipefail

if ! command -v openssl >/dev/null 2>&1; then
  echo "openssl is required for plugin signing." >&2
  exit 1
fi

if [[ $# -lt 1 ]]; then
  echo "Usage: $0 <manifest-path> [payload-root]" >&2
  exit 1
fi

MANIFEST_PATH="$1"
PAYLOAD_ROOT="${2:-}"
if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

SIGNING_KEY_ID="${EMMA_PLUGIN_SIGNING_KEY_ID:-${EMMA_PLUGIN_SIGNATURE_KEY_ID:-}}"
SIGNING_REPOSITORY_ID="${EMMA_PLUGIN_REPOSITORY_ID:-}"
SIGNING_ISSUED_AT_UTC="${EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC:-}"
SIGNING_EXPIRES_AT_UTC="${EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC:-}"

if [[ -z "$SIGNING_KEY_ID" ]]; then
  echo "Set EMMA_PLUGIN_SIGNING_KEY_ID (or EMMA_PLUGIN_SIGNATURE_KEY_ID)." >&2
  exit 1
fi

if [[ -z "$SIGNING_REPOSITORY_ID" ]]; then
  echo "Set EMMA_PLUGIN_REPOSITORY_ID." >&2
  exit 1
fi

KEY_MATERIAL=""
normalize_key_value() {
  local value="$1"

  value="${value//$'\r'/}"
  if [[ ${#value} -ge 2 && "${value:0:1}" == '"' && "${value: -1}" == '"' ]]; then
    value="${value:1:${#value}-2}"
  elif [[ ${#value} -ge 2 && "${value:0:1}" == "'" && "${value: -1}" == "'" ]]; then
    value="${value:1:${#value}-2}"
  fi

  printf '%s' "$value"
}

decode_private_key_material() {
  local value
  local decoded

  value="$(normalize_key_value "$1")"

  if [[ "$value" == *"BEGIN"*"PRIVATE KEY"* ]]; then
    printf '%s' "$value"
    return 0
  fi

  local normalized
  normalized="${value//\\n/$'\n'}"
  normalized="$(normalize_key_value "$normalized")"
  if [[ "$normalized" == *"BEGIN"*"PRIVATE KEY"* ]]; then
    printf '%s' "$normalized"
    return 0
  fi

  if decoded="$(printf '%s' "$value" | base64 --decode 2>/dev/null)" \
    && [[ "$decoded" == *"BEGIN"*"PRIVATE KEY"* ]]; then
    printf '%s' "$(normalize_key_value "$decoded")"
    return 0
  fi

  if decoded="$(printf '%s' "$value" | openssl base64 -d -A 2>/dev/null)" \
    && [[ "$decoded" == *"BEGIN"*"PRIVATE KEY"* ]]; then
    printf '%s' "$(normalize_key_value "$decoded")"
    return 0
  fi

  return 1
}

if [[ -n "${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM:-}" ]]; then
  KEY_MATERIAL="$(decode_private_key_material "${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM}")"
elif [[ -n "${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64:-}" ]]; then
  KEY_MATERIAL="$(decode_private_key_material "${EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64}")"
elif [[ -n "${EMMA_HMAC_KEY_BASE64:-}" ]]; then
  KEY_MATERIAL="$(decode_private_key_material "${EMMA_HMAC_KEY_BASE64}")"
fi

if [[ -z "$KEY_MATERIAL" ]]; then
  echo "Set EMMA_PLUGIN_SIGNING_PRIVATE_KEY_PEM or EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64." >&2
  echo "For unchanged CI workflows, EMMA_HMAC_KEY_BASE64 may contain either base64 PEM or raw PEM private key content." >&2
  exit 1
fi

if [[ "$KEY_MATERIAL" != *"BEGIN"*"PRIVATE KEY"* ]]; then
  echo "Signing key material is not a PEM private key." >&2
  exit 1
fi

KEY_FILE="$(mktemp)"
trap 'rm -f "$KEY_FILE"' EXIT
umask 077
printf '%s\n' "$KEY_MATERIAL" > "$KEY_FILE"

if ! openssl pkey -in "$KEY_FILE" -noout >/dev/null 2>&1; then
  echo "Signing key material could not be parsed as a valid private key." >&2
  echo "Ensure the secret contains an unencrypted PEM private key (raw PEM or base64 PEM)." >&2
  exit 1
fi

export MANIFEST_PATH
export PAYLOAD_ROOT
export KEY_FILE
export SIGNING_KEY_ID
export SIGNING_REPOSITORY_ID
export SIGNING_ISSUED_AT_UTC
export SIGNING_EXPIRES_AT_UTC

python3 - <<'PY'
import base64
import hashlib
import json
import os
import pathlib
import subprocess
import sys
from datetime import datetime, timezone

manifest_path = os.environ.get("MANIFEST_PATH")
payload_root = os.environ.get("PAYLOAD_ROOT")
key_file = os.environ.get("KEY_FILE")
key_id = (os.environ.get("SIGNING_KEY_ID") or "").strip()
repository_id = (os.environ.get("SIGNING_REPOSITORY_ID") or "").strip()
issued_at_utc = (os.environ.get("SIGNING_ISSUED_AT_UTC") or "").strip()
expires_at_utc = (os.environ.get("SIGNING_EXPIRES_AT_UTC") or "").strip()

if not issued_at_utc:
    issued_at_utc = datetime.now(timezone.utc).replace(microsecond=0).isoformat().replace("+00:00", "Z")

with open(manifest_path, "r", encoding="utf-8") as f:
    manifest = json.load(f)

plugin_id = (manifest.get("id") or "").strip()
version = (manifest.get("version") or "").strip()
protocol = (manifest.get("protocol") or "").strip()

if not plugin_id or not version or not protocol:
    print("Manifest must include id, version, and protocol.", file=sys.stderr)
    sys.exit(1)

manifest_without_signature = dict(manifest)
manifest_without_signature.pop("signature", None)
canonical_manifest = json.dumps(
    manifest_without_signature,
    sort_keys=True,
    separators=(",", ":"),
    ensure_ascii=False,
)
manifest_digest_sha256 = hashlib.sha256(canonical_manifest.encode("utf-8")).hexdigest()

def compute_payload_digest(root_dir: str) -> str:
    root = pathlib.Path(root_dir)
    if not root.exists() or not root.is_dir():
        print(f"Payload root does not exist or is not a directory: {root}", file=sys.stderr)
        sys.exit(1)

    hasher = hashlib.sha256()
    files = sorted(p for p in root.rglob("*") if p.is_file())
    for path in files:
        rel = path.relative_to(root).as_posix().encode("utf-8")
        hasher.update(rel)
        hasher.update(b"\0")
        with path.open("rb") as stream:
            while True:
                chunk = stream.read(65536)
                if not chunk:
                    break
                hasher.update(chunk)
        hasher.update(b"\0")

    return hasher.hexdigest()

payload_digest_sha256 = ""
if payload_root:
    payload_digest_sha256 = compute_payload_digest(payload_root)

payload_lines = [
    f"pluginId={plugin_id}",
    f"version={version}",
    f"protocol={protocol}",
    f"repositoryId={repository_id}",
    f"manifestDigestSha256={manifest_digest_sha256}",
    f"payloadDigestSha256={payload_digest_sha256}",
    f"issuedAtUtc={issued_at_utc}",
    f"expiresAtUtc={expires_at_utc}",
]
payload = "\n".join(payload_lines).encode("utf-8")

proc = subprocess.run(
    ["openssl", "dgst", "-sha256", "-sign", key_file],
    input=payload,
    stdout=subprocess.PIPE,
    stderr=subprocess.PIPE,
    check=False,
)
if proc.returncode != 0:
    print(proc.stderr.decode("utf-8", errors="replace"), file=sys.stderr)
    sys.exit(proc.returncode)

signature_b64 = base64.b64encode(proc.stdout).decode("ascii")

signature = manifest.get("signature") or {}
signature["algorithm"] = "rsa-sha256"
signature["value"] = signature_b64
signature["keyId"] = key_id
signature["repositoryId"] = repository_id
signature["issuedAtUtc"] = issued_at_utc
signature["manifestDigestSha256"] = manifest_digest_sha256
signature["payloadDigestSha256"] = payload_digest_sha256
if expires_at_utc:
    signature["expiresAtUtc"] = expires_at_utc
else:
    signature.pop("expiresAtUtc", None)
manifest["signature"] = signature

with open(manifest_path, "w", encoding="utf-8") as f:
    json.dump(manifest, f, indent=2)
    f.write("\n")

print("Updated signature for", manifest_path)
PY
