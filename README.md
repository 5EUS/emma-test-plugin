# EMMA Test Plugin

A minimal gRPC plugin used to validate the host handshake and IPC flow during development.

## Run

```bash
dotnet run --project src/EMMA.TestPlugin/EMMA.TestPlugin.csproj
```

Default port is 5005. Override with:

```bash
dotnet run --project src/EMMA.TestPlugin/EMMA.TestPlugin.csproj -- --port 6001
```

or

```bash
EMMA_TEST_PLUGIN_PORT=6001 dotnet run --project src/EMMA.TestPlugin/EMMA.TestPlugin.csproj
```

## Run with plugin host

Use the helper script from repo root:

```bash
./scripts/run-plugin-host-with-test-plugin.sh
```

## Validate and pack

From repo root, use the canonical pack flow (includes manifest validation):

```bash
./scripts/plugin-pack.sh ./src/EMMA.TestPlugin
```

Build wasm package variant:

```bash
TARGETS="wasm" ./scripts/plugin-pack.sh ./src/EMMA.TestPlugin
```

## Mangadex data

The test plugin queries live data from the Mangadex API by default.

Example:

```bash
dotnet run --project src/EMMA.TestPlugin/EMMA.TestPlugin.csproj
```

Notes:
- `Search` uses `/manga` with safe + suggestive ratings.
- `GetChapters` uses `/manga/{id}/feed`.
- `GetPage` uses `/at-home/server/{chapterId}` and maps the `index` to page files.
TODO no retry/backoff policy
TODO no caching
