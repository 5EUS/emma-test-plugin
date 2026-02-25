#!/usr/bin/env bash
set -euo pipefail

KEY_BYTES=${1:-32}

if ! [[ "$KEY_BYTES" =~ ^[0-9]+$ ]]; then
  echo "Usage: $0 [bytes]" >&2
  exit 1
fi

KEY_BYTES="$KEY_BYTES" python3 - <<'PY'
import base64
import os
import sys

try:
    size = int(os.environ.get("KEY_BYTES", "32"))
except ValueError:
    print("Invalid size", file=sys.stderr)
    sys.exit(1)

key = base64.b64encode(os.urandom(size)).decode("ascii")
print(key)
PY
