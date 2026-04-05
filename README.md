# EMMA Test Plugin

A minimal gRPC plugin used to validate the host handshake and IPC flow during development.

## Run

```bash
dotnet run --project EMMA.TestPlugin.csproj
```

Default port is 5005. Override with:

```bash
dotnet run --project EMMA.TestPlugin.csproj -- --port 6001
```

or

```bash
EMMA_TEST_PLUGIN_PORT=6001 dotnet run --project EMMA.TestPlugin.csproj
```

## Validate and pack

From repo root, use the canonical pack flow (includes manifest validation):

```bash
./scripts/build-pack-plugin.sh ./EMMA.TestPlugin.plugin.json
```

Build wasm package variant:

```bash
TARGETS="wasm" ./scripts/build-pack-plugin.sh ./EMMA.TestPlugin.plugin.json
```

Build regular ASP.NET plugin package variant (for example Linux x64):

```bash
TARGETS="linux-x64" ./scripts/build-pack-plugin-aspnet.sh ./EMMA.TestPlugin.plugin.json
```

## Signing (Delegated RSA)

The packaging scripts now sign manifests with delegated RSA (`rsa-sha256`) and bind signatures to both manifest and payload digests.

Required signing environment variables:

```bash
export EMMA_PLUGIN_SIGNING_KEY_ID="emma-test-shared-release-2026-q2"
export EMMA_PLUGIN_REPOSITORY_ID="emma-test"
export EMMA_PLUGIN_SIGNING_PRIVATE_KEY_BASE64="<base64 pem private key>"
```

Optional signature window metadata:

```bash
export EMMA_PLUGIN_SIGNATURE_ISSUED_AT_UTC="2026-04-04T00:00:00Z"
export EMMA_PLUGIN_SIGNATURE_EXPIRES_AT_UTC="2026-07-01T00:00:00Z"
```

Generate a delegated keypair (script name kept for workflow compatibility):

```bash
./scripts/generate-hmac-key.sh ./.keys emma-test-shared-release-2026-q2
```

For CI compatibility, existing workflows still pass `EMMA_HMAC_KEY_BASE64`; set that secret to the base64 PEM private key value.

## Mangadex data

The test plugin queries live data from the Mangadex API by default.

Example:

```bash
dotnet run --project EMMA.TestPlugin.csproj
```

Notes:
- `Search` uses `/manga` with safe + suggestive ratings.
- `GetChapters` uses `/manga/{id}/feed`.
- `GetPage` uses `/at-home/server/{chapterId}` and maps the `index` to page files.
TODO no retry/backoff policy
TODO no caching