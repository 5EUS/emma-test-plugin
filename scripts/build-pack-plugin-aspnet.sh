#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
ROOT_DIR="$PLUGIN_DIR"
MANIFEST_PATH="${1:-$PLUGIN_DIR/EMMA.TestPlugin.plugin.json}"
OUT_DIR="$PLUGIN_DIR/artifacts"
PACK_DIR="$OUT_DIR/pack"
ASPNET_BUILD_CONFIGURATION="${ASPNET_BUILD_CONFIGURATION:-Release}"
ASPNET_PROJECT_PATH="${ASPNET_PROJECT_PATH:-}"
EMMA_SDK_VERSION="${EMMA_SDK_VERSION:-}"
ASPNET_NO_RESTORE="${ASPNET_NO_RESTORE:-0}"
HOST_OS="$(uname -s)"
DEFAULT_TARGETS="osx-arm64"
if [[ "$HOST_OS" == "Linux" ]]; then
  DEFAULT_TARGETS="linux-x64"
fi
TARGETS="${TARGETS:-$DEFAULT_TARGETS}"

resolve_env_flag() {
  local value="$1"
  if [[ -z "$value" ]]; then
    echo "0"
    return 0
  fi

  case "${value,,}" in
    1|true|yes|on)
      echo "1"
      ;;
    *)
      echo "0"
      ;;
  esac
}

SIGNING_KEY_BASE64="${EMMA_HMAC_KEY_BASE64:-${EMMA_PLUGIN_SIGNATURE_HMAC_KEY_BASE64:-}}"
REQUIRE_SIGNED_PLUGINS_VALUE="${EMMA_REQUIRE_SIGNED_PLUGINS:-${PluginSignature__RequireSignedPlugins:-}}"
REQUIRE_SIGNING="$(resolve_env_flag "$REQUIRE_SIGNED_PLUGINS_VALUE")"
SIGN_MANIFEST_IN_PLACE="${SIGN_MANIFEST_IN_PLACE:-0}"

resolve_project_path() {
  if [[ -n "$ASPNET_PROJECT_PATH" ]]; then
    echo "$ASPNET_PROJECT_PATH"
    return 0
  fi

  local -a csproj_candidates=()
  while IFS= read -r candidate; do
    csproj_candidates+=("$candidate")
  done < <(find "$PLUGIN_DIR" -maxdepth 1 -type f -name "*.csproj" | sort)

  if [[ ${#csproj_candidates[@]} -eq 1 ]]; then
    echo "${csproj_candidates[0]}"
    return 0
  fi

  if [[ ${#csproj_candidates[@]} -eq 0 ]]; then
    echo "No .csproj found in plugin directory: $PLUGIN_DIR" >&2
  else
    echo "Multiple .csproj files found in plugin directory. Set ASPNET_PROJECT_PATH explicitly." >&2
    printf '  - %s\n' "${csproj_candidates[@]}" >&2
  fi

  exit 1
}

if [[ ! -f "$MANIFEST_PATH" ]]; then
  echo "Manifest not found: $MANIFEST_PATH" >&2
  exit 1
fi

if [[ "${MANIFEST_PATH##*.}" == "csproj" ]]; then
  echo "Expected a plugin manifest JSON, but got a .csproj: $MANIFEST_PATH" >&2
  echo "Usage: TARGETS=\"linux-x64\" ./build-pack-plugin-aspnet.sh /path/to/plugin.plugin.json" >&2
  exit 1
fi

PROJECT_PATH="$(resolve_project_path)"
if [[ ! -f "$PROJECT_PATH" ]]; then
  echo "ASP.NET project not found: $PROJECT_PATH" >&2
  exit 1
fi

if [[ -x "$ROOT_DIR/scripts/plugin-validate-manifest.sh" ]]; then
  "$ROOT_DIR/scripts/plugin-validate-manifest.sh" "$MANIFEST_PATH"
fi

if [[ "$REQUIRE_SIGNING" == "1" && -z "$SIGNING_KEY_BASE64" ]]; then
  echo "Signed plugins are required, but no signing key is set." >&2
  echo "Set EMMA_HMAC_KEY_BASE64 (or EMMA_PLUGIN_SIGNATURE_HMAC_KEY_BASE64) before running this script." >&2
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
ENTRYPOINT_NAME=$(echo "$PLUGIN_NAME" | tr -d '[:space:]')
if [[ -z "$ENTRYPOINT_NAME" ]]; then
  ENTRYPOINT_NAME="$PLUGIN_ID"
fi

SIGNABLE_MANIFEST_PATH="$MANIFEST_PATH"
SIGNED_MANIFEST_PATH="$OUT_DIR/${PLUGIN_ID}.signed.plugin.json"
if [[ -n "$SIGNING_KEY_BASE64" ]]; then
  mkdir -p "$OUT_DIR"
  cp "$MANIFEST_PATH" "$SIGNED_MANIFEST_PATH"
  EMMA_HMAC_KEY_BASE64="$SIGNING_KEY_BASE64" "$SCRIPT_DIR/sign-plugin.sh" "$SIGNED_MANIFEST_PATH"
  SIGNABLE_MANIFEST_PATH="$SIGNED_MANIFEST_PATH"

  if [[ "$SIGN_MANIFEST_IN_PLACE" == "1" ]]; then
    cp "$SIGNED_MANIFEST_PATH" "$MANIFEST_PATH"
    echo "Signed source manifest in-place: $MANIFEST_PATH"
  fi

  echo "Signed manifest artifact: $SIGNED_MANIFEST_PATH"
fi

mkdir -p "$PACK_DIR"

for TARGET in $TARGETS; do
  BUILD_DIR="$OUT_DIR/build-$TARGET"
  PUBLISH_DIR="$BUILD_DIR/publish"
  PACKAGE_ROOT="$PACK_DIR/$PLUGIN_VERSION-$TARGET"
  MANIFEST_OUT_DIR="$PACKAGE_ROOT/manifest"
  PLUGIN_OUT_DIR="$PACKAGE_ROOT/$PLUGIN_ID"

  rm -rf "$BUILD_DIR" "$PACKAGE_ROOT" "$PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
  mkdir -p "$PUBLISH_DIR" "$MANIFEST_OUT_DIR" "$PLUGIN_OUT_DIR"

  if [[ "$TARGET" != linux-* && "$TARGET" != osx-* && "$TARGET" != win-* ]]; then
    echo "Unsupported ASP.NET packaging target: $TARGET (supported: linux-*, osx-*, win-*)" >&2
    exit 1
  fi

  publish_args=(
    -c "$ASPNET_BUILD_CONFIGURATION"
    -r "$TARGET"
    --self-contained true
    -p:UseAppHost=true
    -p:PluginTransport=AspNet
    -o "$PUBLISH_DIR"
  )

  if [[ -n "$EMMA_SDK_VERSION" ]]; then
    publish_args+=("-p:EmmaSdkVersion=$EMMA_SDK_VERSION")
  fi

  if [[ "$ASPNET_NO_RESTORE" == "1" ]]; then
    publish_args+=("--no-restore")
  fi

  dotnet publish "$PROJECT_PATH" \
    "${publish_args[@]}"

  APP_RUNTIME_CONFIG=$(find "$PUBLISH_DIR" -maxdepth 1 -type f -name "*.runtimeconfig.json" | head -n 1)
  if [[ -z "$APP_RUNTIME_CONFIG" ]]; then
    echo "Failed to locate runtimeconfig in publish output." >&2
    exit 1
  fi

  APP_EXECUTABLE=$(basename "$APP_RUNTIME_CONFIG" .runtimeconfig.json)

  cp -R "$PUBLISH_DIR"/. "$PLUGIN_OUT_DIR/"

  if [[ "$TARGET" == win-* ]]; then
    if [[ -f "$PLUGIN_OUT_DIR/${APP_EXECUTABLE}.exe" && "${APP_EXECUTABLE}" != "$ENTRYPOINT_NAME" ]]; then
      cp "$PLUGIN_OUT_DIR/${APP_EXECUTABLE}.exe" "$PLUGIN_OUT_DIR/${ENTRYPOINT_NAME}.exe"
    fi
  else
    if [[ -f "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" && "$APP_EXECUTABLE" != "$ENTRYPOINT_NAME" ]]; then
      cp "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" "$PLUGIN_OUT_DIR/$ENTRYPOINT_NAME"
    fi

    chmod +x "$PLUGIN_OUT_DIR/$APP_EXECUTABLE" || true
    chmod +x "$PLUGIN_OUT_DIR/$ENTRYPOINT_NAME" || true
    find "$PLUGIN_OUT_DIR" -type f -name "*.so" -exec chmod +x {} \; || true
  fi

  MANIFEST_OUT="$MANIFEST_OUT_DIR/$PLUGIN_ID.json"
  cp "$SIGNABLE_MANIFEST_PATH" "$MANIFEST_OUT"

  ( cd "$PACKAGE_ROOT" && zip -r "../${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip" . ) >/dev/null

  echo "Packaged ASP.NET plugin: $PACK_DIR/${PLUGIN_ID}_${PLUGIN_VERSION}_${TARGET}.zip"
done
