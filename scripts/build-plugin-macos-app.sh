#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
OUT_DIR="$PLUGIN_DIR/artifacts"
APP_NAME="EMMA.TestPlugin"
MANIFEST_PATH="$PLUGIN_DIR/EMMA.TestPlugin.plugin.json"
PLUGIN_ID="${PLUGIN_ID:-$(python3 -c 'import json,sys;print(json.load(open(sys.argv[1])).get("id",""))' "$MANIFEST_PATH")}" 

if [[ -z "$PLUGIN_ID" ]]; then
  echo "Failed to resolve plugin id from manifest: $MANIFEST_PATH"
  exit 1
fi

PLUGIN_OUT_DIR="$OUT_DIR/$PLUGIN_ID"
APP_DIR="$PLUGIN_OUT_DIR/$APP_NAME.app"
CONTENTS_DIR="$APP_DIR/Contents"
MACOS_DIR="$CONTENTS_DIR/MacOS"
RESOURCES_DIR="$CONTENTS_DIR/Resources"
PUBLISH_DIR="$OUT_DIR/publish"

rm -rf "$APP_DIR"
mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

# Publish self-contained apphost for macOS to avoid system dotnet dependencies.
dotnet publish "$PLUGIN_DIR/EMMA.TestPlugin.csproj" -c Release -r osx-arm64 --self-contained true -p:UseAppHost=true -o "$PUBLISH_DIR"

cp -R "$PUBLISH_DIR"/* "$MACOS_DIR/"

cat > "$CONTENTS_DIR/Info.plist" <<'PLIST'
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleIdentifier</key>
  <string>emma.plugin.test</string>
  <key>CFBundleName</key>
  <string>EMMA.TestPlugin</string>
  <key>CFBundleDisplayName</key>
  <string>EMMA Test Plugin</string>
  <key>CFBundleVersion</key>
  <string>0.1.0</string>
  <key>CFBundleShortVersionString</key>
  <string>0.1.0</string>
  <key>CFBundleExecutable</key>
  <string>EMMA.TestPlugin</string>
  <key>CFBundlePackageType</key>
  <string>APPL</string>
</dict>
</plist>
PLIST

# Ad-hoc sign for dev host execution.
codesign --force --deep --sign - "$APP_DIR"

echo "Built macOS app bundle: $APP_DIR"
