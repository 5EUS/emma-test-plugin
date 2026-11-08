#!/usr/bin/env sh
# Builds the plugin and packs an UNSIGNED development package under rust/dist/:
#   plugin.wasm, plugin.json, and <id>-<version>.zip containing exactly those two files.
# A host only installs an unsigned package when it is configured for development
# (TrustPolicy.RequireSigned = false). Signing is done by the repository's publishing tools.
set -eu
here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

cargo build --release --target wasm32-wasip2 --locked
dist="$here/dist"
rm -rf "$dist"
mkdir -p "$dist"
cp target/wasm32-wasip2/release/emma_mangadex.wasm "$dist/plugin.wasm"

version=$(sed -n 's/^version = "\(.*\)"/\1/p' Cargo.toml | head -n 1)
digest=$(sha256sum "$dist/plugin.wasm" | cut -d ' ' -f 1)

cat > "$dist/plugin.json" <<JSON
{
  "schema": 1,
  "id": "emma.plugin.mangadex",
  "name": "MangaDex",
  "version": "$version",
  "description": "Browse and read manga from MangaDex.",
  "author": "projectemma",
  "kinds": ["manga", "manga.chapter", "manga.page"],
  "contract": {
    "required": ["emma:core/meta@1", "emma:core/catalog@1", "emma:core/assets@1"],
    "optional": ["emma:core/search-assist@1"]
  },
  "transport": "wasm",
  "entrypoint": { "path": "plugin.wasm", "sha256": "$digest" },
  "permissions": {
    "network": ["api.mangadex.org", "uploads.mangadex.org", "*.mangadex.network"],
    "cache": true
  },
  "limits": { "cpuBudgetMs": 1000, "memoryMb": 64 }
}
JSON

(cd "$dist" && python3 -I -m zipfile -c "emma.plugin.mangadex-$version.zip" plugin.json plugin.wasm)
echo "packed $dist/emma.plugin.mangadex-$version.zip (sha256 $digest)"
