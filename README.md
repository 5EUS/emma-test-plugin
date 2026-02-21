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

## Demo data

- Search query: `demo` returns media ids `demo-1` and `demo-2`.
- Chapters: `demo-1` returns chapter `ch-1`.
- Page: `demo-1` + `ch-1` + index `0` returns a demo page URI.
- Video: `demo-video-1` returns stream `stream-1` and a sample segment payload.

## Fixture

Edit [src/EMMA.TestPlugin/fixture.json](src/EMMA.TestPlugin/fixture.json) to tweak demo data.

To point at a custom file, set `EMMA_TEST_PLUGIN_FIXTURE` to an absolute path.
