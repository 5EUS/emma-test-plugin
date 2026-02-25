#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR=$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)
PLUGIN_DIR=$(cd "$SCRIPT_DIR/.." && pwd)
OUT_DIR="$PLUGIN_DIR/artifacts"
PUBLISH_DIR="$OUT_DIR/publish"

rm -rf "$PUBLISH_DIR"
mkdir -p "$PUBLISH_DIR"

dotnet publish "$PLUGIN_DIR/EMMA.PluginTemplate.csproj" -c Release -o "$PUBLISH_DIR"

ENTRYPOINT="EMMA.PluginTemplate"
if [[ "$OSTYPE" == "msys"* || "$OSTYPE" == "cygwin"* || "$OSTYPE" == "win32"* ]]; then
  ENTRYPOINT+=".exe"
fi

echo "Built plugin to: $PUBLISH_DIR"
echo "Entrypoint: $ENTRYPOINT"
