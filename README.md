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
