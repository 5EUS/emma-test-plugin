#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
REPO_ROOT="$PLUGIN_DIR"

GLOBAL_JSON_PATH="$REPO_ROOT/global.json"
DEFAULT_DOCKER_IMAGE="mcr.microsoft.com/dotnet/sdk:10.0-preview"
if [[ -f "$GLOBAL_JSON_PATH" ]]; then
  sdk_version=$(python3 - "$GLOBAL_JSON_PATH" <<'PY'
import json
import sys

path = sys.argv[1]
try:
    with open(path, "r", encoding="utf-8") as f:
        data = json.load(f)
    version = data.get("sdk", {}).get("version", "")
    print(version if isinstance(version, str) else "")
except Exception:
    print("")
PY
)
  if [[ -n "$sdk_version" ]]; then
    major_minor="$(echo "$sdk_version" | sed -E 's/^([0-9]+\.[0-9]+).*/\1/')"
    if [[ "$major_minor" == "$sdk_version" ]]; then
      major_minor=""
    fi

    candidates=("mcr.microsoft.com/dotnet/sdk:$sdk_version")
    if [[ "$sdk_version" == *preview* && -n "$major_minor" ]]; then
      candidates+=("mcr.microsoft.com/dotnet/sdk:${major_minor}-preview")
    fi
    if [[ -n "$major_minor" ]]; then
      candidates+=("mcr.microsoft.com/dotnet/sdk:${major_minor}")
    fi

    resolved_image=""
    for candidate in "${candidates[@]}"; do
      if docker manifest inspect "$candidate" >/dev/null 2>&1; then
        resolved_image="$candidate"
        break
      fi
    done

    if [[ -n "$resolved_image" ]]; then
      DEFAULT_DOCKER_IMAGE="$resolved_image"
    fi
  fi
fi

DOCKER_IMAGE=${DOCKER_IMAGE:-$DEFAULT_DOCKER_IMAGE}
DOCKER_PLATFORM=${DOCKER_PLATFORM:-linux/amd64}
NUGET_VOLUME_NAME=${NUGET_VOLUME_NAME:-emma-nuget}
WASI_SDK_HOST_PATH=${WASI_SDK_HOST_PATH:-$REPO_ROOT/../wasi-sdk}
TARGETS=${TARGETS:-wasm}

if [[ ! -d "$WASI_SDK_HOST_PATH" ]]; then
  echo "WASI SDK host path not found: $WASI_SDK_HOST_PATH" >&2
  echo "Set WASI_SDK_HOST_PATH to the extracted wasi-sdk directory." >&2
  exit 1
fi

manifest_arg=${1:-$PLUGIN_DIR/EMMA.TestPlugin.plugin.json}
if [[ "$manifest_arg" = /* ]]; then
  manifest_host_path="$manifest_arg"
else
  manifest_host_path="$(cd "$PWD" && cd "$(dirname "$manifest_arg")" && pwd)/$(basename "$manifest_arg")"
fi

if [[ ! -f "$manifest_host_path" ]]; then
  echo "Manifest not found: $manifest_host_path" >&2
  exit 1
fi

case "$manifest_host_path" in
  "$REPO_ROOT"/*)
    manifest_container_path="/work/${manifest_host_path#"$REPO_ROOT"/}"
    ;;
  *)
    echo "Manifest must be inside repository root: $REPO_ROOT" >&2
    exit 1
    ;;
esac

docker run --rm \
  --platform "$DOCKER_PLATFORM" \
  -v "$REPO_ROOT:/work" \
  -v "$WASI_SDK_HOST_PATH:/opt/wasi-sdk:ro" \
  -v "$NUGET_VOLUME_NAME:/root/.nuget/packages" \
  -w /tmp \
  "$DOCKER_IMAGE" \
  bash -lc "apt-get update >/dev/null && apt-get install -y --no-install-recommends python3 zip >/dev/null && TARGETS='$TARGETS' WASI_SDK_PATH=/opt/wasi-sdk /work/scripts/build-pack-plugin.sh '$manifest_container_path'"
