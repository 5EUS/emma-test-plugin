#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.PluginTemplate.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
TARGETS=${TARGETS:-"osx-arm64"}

if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
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
version = manifest.get("version") or "0.0.0"

print(plugin_id)
print(plugin_name)
print(version)
PY
)

if [[ ${#manifest_fields[@]} -lt 3 ]]; then
  echo "Failed to parse manifest fields." >&2
  exit 1
fi

PLUGIN_ID="${manifest_fields[0]}"
PLUGIN_NAME="${manifest_fields[1]}"
PLUGIN_VERSION="${manifest_fields[2]}"
APP_BUNDLE_NAME=$(echo "$PLUGIN_NAME" | tr -d '[:space:]')
if [[ -z "$APP_BUNDLE_NAME" ]]; then
  APP_BUNDLE_NAME="$PLUGIN_ID"
fi

APP_NAME="$APP_BUNDLE_NAME.app"
mkdir -p "$PACK_DIR"

for TARGET in $TARGETS; do
  if [[ "$TARGET" != osx-* ]]; then
    echo "Unsupported target for .app packaging: $TARGET" >&2
    exit 1
  fi

  BUILD_DIR="$OUT_DIR/build-$TARGET"
  PUBLISH_DIR="$BUILD_DIR/publish"
  APP_DIR="$BUILD_DIR/$APP_NAME"
  CONTENTS_DIR="$APP_DIR/Contents"
  MACOS_DIR="$CONTENTS_DIR/MacOS"
  RESOURCES_DIR="$CONTENTS_DIR/Resources"

  rm -rf "$BUILD_DIR" "$PACK_DIR/$PLUGIN_VERSION-$TARGET" "$PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
  mkdir -p "$PUBLISH_DIR" "$MACOS_DIR" "$RESOURCES_DIR" "$PACK_DIR/$PLUGIN_VERSION-$TARGET"

  # Publish self-contained apphost for macOS to avoid system dotnet dependencies.
  dotnet publish "$PLUGIN_DIR/EMMA.PluginTemplate.csproj" -c Release -r "$TARGET" --self-contained true -p:UseAppHost=true -o "$PUBLISH_DIR"

  APP_RUNTIME_CONFIG=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.runtimeconfig.json" | head -n 1)
  if [[ -z "$APP_RUNTIME_CONFIG" ]]; then
    echo "Failed to locate runtimeconfig in publish output." >&2
    exit 1
  fi

  APP_EXECUTABLE=$(basename "$APP_RUNTIME_CONFIG" .runtimeconfig.json)

  cp -R "$PUBLISH_DIR"/. "$MACOS_DIR/"
  rm -rf "$MACOS_DIR/artifacts"

  cat > "$CONTENTS_DIR/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>$PLUGIN_ID</string>
  <key>CFBundleName</key>
  <string>$PLUGIN_NAME</string>
  <key>CFBundleDisplayName</key>
  <string>$PLUGIN_NAME</string>
  <key>CFBundleVersion</key>
  <string>$PLUGIN_VERSION</string>
  <key>CFBundleShortVersionString</key>
  <string>$PLUGIN_VERSION</string>
  <key>CFBundleExecutable</key>
  <string>$APP_EXECUTABLE</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
</dict>
</plist>
PLIST

  codesign --force --deep --sign - --entitlements "$PLUGIN_DIR/entitlements.plist" "$APP_DIR"

  MANIFEST_OUT_DIR="$PACK_DIR/$PLUGIN_VERSION-$TARGET/manifest"
  PLUGIN_OUT_DIR="$PACK_DIR/$PLUGIN_VERSION-$TARGET/$PLUGIN_ID"
  mkdir -p "$MANIFEST_OUT_DIR" "$PLUGIN_OUT_DIR"

  MANIFEST_OUT="$MANIFEST_OUT_DIR/$PLUGIN_ID.json"
  cp "$MANIFEST_PATH" "$MANIFEST_OUT"

  if [[ -n "${EMMA_HMAC_KEY_BASE64:-}" ]]; then
    "$SCRIPT_DIR/sign-plugin.sh" "$MANIFEST_OUT"
  fi

  cp -R "$APP_DIR" "$PLUGIN_OUT_DIR/"

  ( cd "$PACK_DIR/$PLUGIN_VERSION-$TARGET" && zip -r "../${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip" . ) >/dev/null

  echo "Packaged plugin: $PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
done
