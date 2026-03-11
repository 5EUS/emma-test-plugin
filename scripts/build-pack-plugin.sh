#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
ROOT_DIR=$(cd "$PLUGIN_DIR/../.." && pwd)
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.TestPlugin.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
HOST_OS="$(uname -s)"
DEFAULT_TARGETS="osx-arm64"
if [[ "$HOST_OS" == "Linux" ]]; then
  DEFAULT_TARGETS="linux-x64"
fi
TARGETS=${TARGETS:-"$DEFAULT_TARGETS"}
WASM_MODULE_PATH="${WASM_MODULE_PATH:-$OUT_DIR/wasm/plugin.wasm}"
WASM_PACKAGE_FILE_NAME="${WASM_PACKAGE_FILE_NAME:-plugin.wasm}"
WASM_PROJECT_PATH="${WASM_PROJECT_PATH:-$PLUGIN_DIR/EMMA.TestPlugin.csproj}"
WASM_BUILD_CONFIGURATION="${WASM_BUILD_CONFIGURATION:-Release}"
WASM_BUILD_RID="${WASM_BUILD_RID:-wasi-wasm}"
WASM_BUILD_OUTPUT="${WASM_BUILD_OUTPUT:-$OUT_DIR/wasm-publish}"
WASM_OUTPUT_NAME="${WASM_OUTPUT_NAME:-}"
SKIP_WASM_BUILD="${SKIP_WASM_BUILD:-0}"
CWASM_WASMTIME_TARGET="${CWASM_WASMTIME_TARGET:-}"
CWASM_WASMTIME_BIN="${CWASM_WASMTIME_BIN:-wasmtime}"
CWASM_EXPECTED_WASMTIME_VERSION="${CWASM_EXPECTED_WASMTIME_VERSION:-34.0.2}"
CWASM_PRECOMPILE_TOOL="${CWASM_PRECOMPILE_TOOL:-$ROOT_DIR/tools/EMMA.CwasmPrecompile/target/release/emma_cwasm_precompile}"

resolve_default_cwasm_target() {
  local rust_host
  rust_host="$(rustc -vV 2>/dev/null | awk '/^host:/ {print $2}')"
  if [[ -n "$rust_host" ]]; then
    echo "$rust_host"
    return 0
  fi

  case "$(uname -s)-$(uname -m)" in
    Darwin-arm64)
      echo "aarch64-apple-darwin"
      ;;
    Darwin-x86_64)
      echo "x86_64-apple-darwin"
      ;;
    Linux-x86_64)
      echo "x86_64-unknown-linux-gnu"
      ;;
    Linux-aarch64)
      echo "aarch64-unknown-linux-gnu"
      ;;
    *)
      echo ""
      ;;
  esac
}

run_precompile_tool() {
  local input_wasm="$1"
  local output_cwasm="$2"
  local compile_target="$3"

  if [[ ! -x "$CWASM_PRECOMPILE_TOOL" ]]; then
    return 1
  fi

  "$CWASM_PRECOMPILE_TOOL" "$input_wasm" "$output_cwasm" "$compile_target"

  return 0
}

build_precompiled_cwasm() {
  local input_wasm="$1"
  local output_cwasm="$2"
  local compile_target="$3"

  if [[ ! -f "$input_wasm" ]]; then
    echo "Input wasm component not found: $input_wasm" >&2
    exit 1
  fi

  mkdir -p "$(dirname "$output_cwasm")"

  if [[ -z "$compile_target" ]]; then
    echo "Failed to resolve cwasm compile target." >&2
    exit 1
  fi

  if run_precompile_tool "$input_wasm" "$output_cwasm" "$compile_target"; then
    :
  else
    if ! command -v "$CWASM_WASMTIME_BIN" >/dev/null 2>&1; then
      echo "TARGETS includes cwasm but no compatible precompiler is available." >&2
      echo "Either build $CWASM_PRECOMPILE_TOOL or install Wasmtime ${CWASM_EXPECTED_WASMTIME_VERSION} and set CWASM_WASMTIME_BIN." >&2
      exit 1
    fi

    local wasmtime_version
    wasmtime_version="$($CWASM_WASMTIME_BIN --version 2>/dev/null | awk '{print $2}')"
    if [[ -n "$CWASM_EXPECTED_WASMTIME_VERSION" && "$wasmtime_version" != "$CWASM_EXPECTED_WASMTIME_VERSION" ]]; then
      echo "Incompatible Wasmtime CLI version for cwasm precompile: found $wasmtime_version, expected $CWASM_EXPECTED_WASMTIME_VERSION" >&2
      echo "Set CWASM_WASMTIME_BIN to a Wasmtime $CWASM_EXPECTED_WASMTIME_VERSION binary, or build the local precompile tool." >&2
      exit 1
    fi

    "$CWASM_WASMTIME_BIN" compile --target "$compile_target" -o "$output_cwasm" "$input_wasm"
  fi

  local header_hex
  header_hex="$(python3 - "$output_cwasm" <<'PY'
import pathlib
import sys

path = pathlib.Path(sys.argv[1])
with path.open('rb') as f:
    head = f.read(4)
print(head.hex())
PY
)"

  if [[ "$header_hex" != "7f454c46" ]]; then
    echo "Generated cwasm does not look like a precompiled ELF artifact: $output_cwasm" >&2
    exit 1
  fi
}

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

if [[ -x "$ROOT_DIR/scripts/plugin-validate-manifest.sh" ]]; then
  "$ROOT_DIR/scripts/plugin-validate-manifest.sh" "$MANIFEST_PATH"
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

    if command -v codesign >/dev/null 2>&1; then
      codesign --force --deep --sign - --entitlements "$PLUGIN_DIR/entitlements.plist" "$APP_DIR"
    else
      echo "Warning: codesign not found; skipping macOS app signing for $APP_DIR" >&2
    fi
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

    cp -R "$PUBLISH_DIR"/. "$PLUGIN_OUT_DIR/"
    if [[ -f "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" && "$APP_EXECUTABLE" != "$ENTRYPOINT_NAME" ]]; then
      cp "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" "$PLUGIN_OUT_DIR/$ENTRYPOINT_NAME"
    fi

    chmod +x "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" || true
    chmod +x "$PLUGIN_OUT_DIR/$ENTRYPOINT_NAME" || true
    find "$PLUGIN_OUT_DIR" -type f -name "*.so" -exec chmod +x {} \; || true

    while IFS= read -r candidate; do
      if file -b "$candidate" | grep -qiE 'ELF .*executable|ELF .*shared object'; then
        chmod +x "$candidate" || true
      fi
    done < <(find "$PLUGIN_OUT_DIR" -type f)
  elif [[ "$TARGET" == wasm* || "$TARGET" == cwasm* ]]; then
    if [[ "$SKIP_WASM_BUILD" != "1" ]]; then
      build_wasm_component
    fi

    if [[ ! -f "$WASM_MODULE_PATH" ]]; then
      echo "WASM component not found: $WASM_MODULE_PATH" >&2
      echo "Build failed or was skipped. Set WASM_MODULE_PATH=/absolute/path/plugin.wasm (or .cwasm) or leave SKIP_WASM_BUILD=0 to compile automatically." >&2
      exit 1
    fi

    mkdir -p "$PLUGIN_OUT_DIR/wasm"
    package_file_name="$WASM_PACKAGE_FILE_NAME"
    if [[ "$TARGET" == cwasm* ]]; then
      package_file_name="plugin.cwasm"
      cwasm_source="$WASM_MODULE_PATH"
      cwasm_compile_target="$CWASM_WASMTIME_TARGET"
      if [[ -z "$cwasm_compile_target" ]]; then
        cwasm_compile_target="$(resolve_default_cwasm_target)"
      fi
      if [[ "${WASM_MODULE_PATH##*.}" != "cwasm" ]]; then
        cwasm_source="$BUILD_DIR/plugin.cwasm"
        build_precompiled_cwasm "$WASM_MODULE_PATH" "$cwasm_source" "$cwasm_compile_target"
      fi

      cp "$cwasm_source" "$PLUGIN_OUT_DIR/wasm/$package_file_name"
    else
      cp "$WASM_MODULE_PATH" "$PLUGIN_OUT_DIR/wasm/$package_file_name"
    fi
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
