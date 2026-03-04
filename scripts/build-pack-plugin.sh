#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.TestPlugin.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
TARGETS=${TARGETS:-"osx-arm64"}
WASM_MODULE_PATH="${WASM_MODULE_PATH:-$OUT_DIR/wasm/plugin.wasm}"
WASM_PROJECT_PATH="${WASM_PROJECT_PATH:-$PLUGIN_DIR/EMMA.TestPlugin.csproj}"
WASM_BUILD_CONFIGURATION="${WASM_BUILD_CONFIGURATION:-Release}"
WASM_BUILD_RID="${WASM_BUILD_RID:-wasi-wasm}"
WASM_BUILD_OUTPUT="${WASM_BUILD_OUTPUT:-$OUT_DIR/wasm-publish}"
WASM_OUTPUT_NAME="${WASM_OUTPUT_NAME:-}"
SKIP_WASM_BUILD="${SKIP_WASM_BUILD:-0}"

build_wasm_component() {
  if [[ ! -f "$WASM_PROJECT_PATH" ]]; then
    echo "WASM project not found: $WASM_PROJECT_PATH" >&2
    exit 1
  fi

  rm -rf "$WASM_BUILD_OUTPUT"
  mkdir -p "$WASM_BUILD_OUTPUT"

  if [[ "$WASM_BUILD_RID" == "wasi-wasm" ]]; then
    if [[ -z "${WASI_SDK_PATH:-}" ]]; then
      echo "WASM build target '$WASM_BUILD_RID' requires WASI SDK." >&2
      echo "Set WASI_SDK_PATH to your extracted wasi-sdk directory (for example /opt/wasi-sdk-22.0)." >&2
      echo "Download: https://github.com/WebAssembly/wasi-sdk/releases" >&2
      exit 1
    fi

    if [[ ! -d "$WASI_SDK_PATH" ]]; then
      echo "WASI_SDK_PATH does not exist: $WASI_SDK_PATH" >&2
      exit 1
    fi
  fi

  echo "Compiling wasm component from project: $WASM_PROJECT_PATH"
  dotnet publish "$WASM_PROJECT_PATH" \
    -c "$WASM_BUILD_CONFIGURATION" \
    -r "$WASM_BUILD_RID" \
    --self-contained true \
    -p:PublishAot=false \
    -p:WasmSingleFileBundle=true \
    -p:PluginTransport=Wasm \
    -o "$WASM_BUILD_OUTPUT"

  local expected_name
  if [[ -n "$WASM_OUTPUT_NAME" ]]; then
    expected_name="$WASM_OUTPUT_NAME"
  else
    expected_name="$(basename "$WASM_PROJECT_PATH" .csproj).wasm"
  fi

  local project_dir
  project_dir="$(dirname "$WASM_PROJECT_PATH")"

  local built_wasm
  built_wasm="$(find "$project_dir/bin/$WASM_BUILD_CONFIGURATION" -type f -path "*/$WASM_BUILD_RID/AppBundle/$expected_name" 2>/dev/null | head -n 1)"

  if [[ -z "$built_wasm" ]]; then
    built_wasm="$(find "$project_dir/bin/$WASM_BUILD_CONFIGURATION" -type f -path "*/$WASM_BUILD_RID/AppBundle/*.wasm" ! -name "dotnet.wasm" 2>/dev/null | head -n 1)"
  fi

  if [[ -z "$built_wasm" ]]; then
    built_wasm="$(find "$WASM_BUILD_OUTPUT" -type f -name "$expected_name" 2>/dev/null | head -n 1)"
  fi

  if [[ -z "$built_wasm" ]]; then
    built_wasm="$(find "$WASM_BUILD_OUTPUT" -type f -name "*.wasm" ! -name "dotnet.wasm" 2>/dev/null | head -n 1)"
  fi

  if [[ -z "$built_wasm" ]]; then
    echo "No bundled .wasm output found for: $expected_name" >&2
    echo "Ensure the project is wasm-capable (for example, target wasi-wasm) or set WASM_MODULE_PATH to an existing component." >&2
    exit 1
  fi

  local header_hex
  header_hex="$(python3 - "$built_wasm" <<'PY'
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
with path.open('rb') as f:
    head = f.read(8)
print(head.hex())
PY
)"

  if [[ "$header_hex" != "0061736d0d000100" ]]; then
    echo "Incompatible wasm artifact produced: $built_wasm" >&2
    echo "This file is a core WebAssembly module (or unknown format), but EMMA.PluginHost now only accepts WebAssembly components (version 13)." >&2
    echo "Use a component artifact via WASM_MODULE_PATH." >&2
    exit 1
  fi

  mkdir -p "$(dirname "$WASM_MODULE_PATH")"
  cp "$built_wasm" "$WASM_MODULE_PATH"
  echo "WASM component ready: $WASM_MODULE_PATH"
}

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
  BUILD_DIR="$OUT_DIR/build-$TARGET"
  PUBLISH_DIR="$BUILD_DIR/publish"
  PACKAGE_ROOT="$PACK_DIR/$PLUGIN_VERSION-$TARGET"
  MANIFEST_OUT_DIR="$PACKAGE_ROOT/manifest"
  PLUGIN_OUT_DIR="$PACKAGE_ROOT/$PLUGIN_ID"

  rm -rf "$BUILD_DIR" "$PACKAGE_ROOT" "$PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
  mkdir -p "$PUBLISH_DIR" "$MANIFEST_OUT_DIR" "$PLUGIN_OUT_DIR"

  if [[ "$TARGET" == osx-* ]]; then
    APP_DIR="$BUILD_DIR/$APP_NAME"
    CONTENTS_DIR="$APP_DIR/Contents"
    MACOS_DIR="$CONTENTS_DIR/MacOS"
    RESOURCES_DIR="$CONTENTS_DIR/Resources"

    mkdir -p "$MACOS_DIR" "$RESOURCES_DIR"

    dotnet publish "$PLUGIN_DIR/EMMA.TestPlugin.csproj" -c Release -r "$TARGET" --self-contained true -p:UseAppHost=true -o "$PUBLISH_DIR"

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
    cp -R "$APP_DIR" "$PLUGIN_OUT_DIR/"
  elif [[ "$TARGET" == linux-* ]]; then
    dotnet publish "$PLUGIN_DIR/EMMA.TestPlugin.csproj" -c Release -r "$TARGET" --self-contained true -p:UseAppHost=true -o "$PUBLISH_DIR"

    APP_RUNTIME_CONFIG=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.runtimeconfig.json" | head -n 1)
    if [[ -z "$APP_RUNTIME_CONFIG" ]]; then
      echo "Failed to locate runtimeconfig in publish output." >&2
      exit 1
    fi

    APP_EXECUTABLE=$(basename "$APP_RUNTIME_CONFIG" .runtimeconfig.json)
    ENTRYPOINT_NAME="$APP_BUNDLE_NAME"

    mkdir -p "$PLUGIN_OUT_DIR/linux"
    cp -R "$PUBLISH_DIR"/. "$PLUGIN_OUT_DIR/linux/"
    if [[ -f "$PLUGIN_OUT_DIR/linux/$APP_EXECUTABLE" && "$APP_EXECUTABLE" != "$ENTRYPOINT_NAME" ]]; then
      cp "$PLUGIN_OUT_DIR/linux/$APP_EXECUTABLE" "$PLUGIN_OUT_DIR/linux/$ENTRYPOINT_NAME"
    fi

    chmod +x "$PLUGIN_OUT_DIR/linux/$APP_EXECUTABLE" || true
    chmod +x "$PLUGIN_OUT_DIR/linux/$ENTRYPOINT_NAME" || true
    find "$PLUGIN_OUT_DIR/linux" -maxdepth 1 -type f -name "*.so" -exec chmod +x {} \; || true
  elif [[ "$TARGET" == wasm* ]]; then
    if [[ "$SKIP_WASM_BUILD" != "1" ]]; then
      build_wasm_component
    fi

    if [[ ! -f "$WASM_MODULE_PATH" ]]; then
      echo "WASM component not found: $WASM_MODULE_PATH" >&2
      echo "Build failed or was skipped. Set WASM_MODULE_PATH=/absolute/path/plugin.wasm or leave SKIP_WASM_BUILD=0 to compile automatically." >&2
      exit 1
    fi

    mkdir -p "$PLUGIN_OUT_DIR/wasm"
    cp "$WASM_MODULE_PATH" "$PLUGIN_OUT_DIR/wasm/plugin.wasm"
  else
    echo "Unsupported target for packaging: $TARGET" >&2
    exit 1
  fi

  MANIFEST_OUT="$MANIFEST_OUT_DIR/$PLUGIN_ID.json"
  cp "$MANIFEST_PATH" "$MANIFEST_OUT"

  if [[ -n "${EMMA_HMAC_KEY_BASE64:-}" ]]; then
    "$SCRIPT_DIR/sign-plugin.sh" "$MANIFEST_OUT"
  fi

  ( cd "$PACKAGE_ROOT" && zip -r "../${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip" . ) >/dev/null

  echo "Packaged plugin: $PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
done
