#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
RID="${1:-linux-x64}"
OUT_DIR="$PLUGIN_DIR/artifacts"
MANIFEST_PATH="$PLUGIN_DIR/EMMA.TestPlugin.plugin.json"

if [[ "$RID" != linux-* ]]; then
  echo "Unsupported RID for Linux bundle build: $RID"
  exit 1
fi

manifest_fields=()
while IFS= read -r line; do
  manifest_fields+=("$line")
done < <(python3 - "$MANIFEST_PATH" <<'PY'
import json
import sys

manifest_path = sys.argv[1]
with open(manifest_path, "r", encoding="utf-8") as f:
    manifest = json.load(f)

plugin_id = manifest.get("id") or "plugin"
plugin_name = manifest.get("name") or plugin_id
entrypoint = "".join(ch for ch in plugin_name if not ch.isspace()) or plugin_id

print(plugin_id)
print(entrypoint)
PY
)

if [[ ${#manifest_fields[@]} -lt 2 ]]; then
  echo "Failed to parse plugin metadata from manifest."
  exit 1
fi

PLUGIN_ID="${manifest_fields[0]}"
ENTRYPOINT_NAME="${manifest_fields[1]}"
PLUGIN_OUT_DIR="$OUT_DIR/$PLUGIN_ID"
PLUGIN_ROOT="$PLUGIN_OUT_DIR"
PUBLISH_DIR="$OUT_DIR/publish-$RID"

rm -rf "$PLUGIN_ROOT" "$PUBLISH_DIR"
mkdir -p "$PLUGIN_ROOT" "$PUBLISH_DIR"

dotnet publish "$PLUGIN_DIR/EMMA.TestPlugin.csproj" \
  -c Release \
  -r "$RID" \
  --self-contained true \
  -p:UseAppHost=true \
  -o "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR"/. "$PLUGIN_ROOT/"

APP_RUNTIME_CONFIG=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.runtimeconfig.json" | head -n 1)
if [[ -z "$APP_RUNTIME_CONFIG" ]]; then
  echo "Failed to locate runtimeconfig in publish output." >&2
  exit 1
fi

APP_EXECUTABLE=$(basename "$APP_RUNTIME_CONFIG" .runtimeconfig.json)
if [[ -f "$PLUGIN_ROOT/$APP_EXECUTABLE" && "$APP_EXECUTABLE" != "$ENTRYPOINT_NAME" ]]; then
  cp "$PLUGIN_ROOT/$APP_EXECUTABLE" "$PLUGIN_ROOT/$ENTRYPOINT_NAME"
fi

chmod +x "$PLUGIN_ROOT/$ENTRYPOINT_NAME" || true
chmod +x "$PLUGIN_ROOT/$APP_EXECUTABLE" || true
find "$PLUGIN_ROOT" -maxdepth 1 -type f -name "*.so" -exec chmod +x {} \; || true

echo "Built Linux plugin bundle: $PLUGIN_ROOT"
echo "Entrypoint: $ENTRYPOINT_NAME"