#!/usr/bin/env bash
set -euo pipefail

OUT_DIR="${1:-.keys}"
KEY_NAME="${2:-delegated-signer}"
KEY_BITS="${3:-3072}"

if ! [[ "$KEY_BITS" =~ ^[0-9]+$ ]]; then
  echo "Usage: $0 [out-dir] [key-name] [bits]" >&2
  exit 1
fi

mkdir -p "$OUT_DIR"
PRIVATE_KEY_PATH="$OUT_DIR/$KEY_NAME.private.pem"
PUBLIC_KEY_PATH="$OUT_DIR/$KEY_NAME.public.pem"

umask 077
openssl genpkey -algorithm RSA -pkeyopt "rsa_keygen_bits:$KEY_BITS" -out "$PRIVATE_KEY_PATH" >/dev/null 2>&1
openssl rsa -in "$PRIVATE_KEY_PATH" -pubout -out "$PUBLIC_KEY_PATH" >/dev/null 2>&1

echo "Generated private key: $PRIVATE_KEY_PATH"
echo "Generated public key:  $PUBLIC_KEY_PATH"

echo
echo "Set GitHub secret EMMA_HMAC_KEY_BASE64 (legacy name) to this base64 PEM value:"
base64 -w 0 "$PRIVATE_KEY_PATH"
echo
