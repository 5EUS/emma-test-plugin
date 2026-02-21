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
