# EMMA Test Plugin

A reference implementation of the EMMA plugin SDK showing best practices for
both ASP.NET and WASM transports with minimal example behavior (Mangadex API).

For `v0.7.0`, this sample is intentionally paged-first. The dedicated reference
path for standardized video support lives in `emma-video-test`.

The repository now uses explicit transport projects so every IDE can analyze the
transport you opened directly instead of relying on design-time MSBuild property
switching:

- `EMMA.TestPlugin.Core.csproj`: transport-agnostic provider logic
- `EMMA.TestPlugin.csproj`: ASP.NET host transport
- `EMMA.TestPlugin.Wasm.csproj`: WASM transport

## Architecture Overview

The plugin is organized into three distinct layers:

### 1. Domain Layer (`Infrastructure/CoreClient.cs`)
The core business logic that is transport-agnostic. For a real plugin, this would
contain your provider API integration, data mapping, and caching logic. In the test
plugin, it demonstrates search, chapter, and page retrieval from a live API.

**What to customize:** Replace CoreClient's calls to your provider's API.

### 2. Transport Adapter Layer
Adapts domain behavior to the plugin IPC protocol. Kept separate by transport:

- **ASP.NET** (`Program.cs` + `Services/AspNetClient.cs`):
  Handles gRPC server lifecycle and dependency injection. Domain calls via injected
  `AspNetClient`.
  
- **WASM** (`Program.cs` + `WASM/WasmGlue.cs` + `WASM/WasmClient.cs`):
  Coordinates CLI entry points, operation dispatch, JSON context, and WIT export helpers.
  Domain calls still flow through `WasmClient`.

**What to avoid:** Don't put domain logic here. Keep this layer focused on protocol
negotiation and type conversion.

### 3. SDK Helper Layer (`EMMA.Plugin.Common` package)
Reusable infrastructure that handles payload precedence, operation routing, and
type mapping. Used consistently across both transports.

Key helpers:
- `PluginPayloadResolvers`: Implements payload precedence (provided > fetched > empty)
- `PluginOperationDispatcher`: Routes operations and handles errors consistently
- `PluginWasmPagingJsonHelpers`: Handles pagination for large chapter feeds
- `PluginSearchUrlResolver`: Centralizes search URL resolution
- `PluginInvokeHelper`: Generic typed dispatch for WASM WIT exports

**What to use:** These are production-ready. Rely on them; don't reimplement.

## Understanding the Transport Split

Check `Program.cs`: the `#if PLUGIN_TRANSPORT_ASPNET` / `#else` structure separates
the two transports at compile time, but each branch is now compiled by a fixed
project instead of a property-switched design-time build:

- ASP.NET path: Full DI, HTTP headers, gRPC server lifecycle
- WASM path: CLI operation dispatch, JSON serialization, WIT component exports

This split is deliberate to keep transport concerns isolated and the code readable.
If you're starting a new plugin, **choose one transport first**, get it working,
then add the other transport if needed.

For the `v0.7.0` golden path, treat this repository as the paged-media sample
and `emma-video-test` as the dedicated video-validation sample.

## Run

```bash
dotnet run --project EMMA.TestPlugin.csproj
```

Default port is 5005. Override with:

```bash
dotnet run --project EMMA.TestPlugin.csproj -- --port 6001
```

Build the WASM transport with:

```bash
WASI_SDK_PATH=/path/to/wasi-sdk dotnet build EMMA.TestPlugin.Wasm.csproj
```

## WASM Troubleshooting

- If WASM search or invoke fails with `failed:A type initializer threw an exception`, check for circular static initialization first.
- In this plugin, the failure came from types reaching into `MangadexPluginBundle.Instance` during their own static field initialization.
- Prefer leaf singletons such as `MangadexProviderClient.Instance` in static fields when the code only needs the provider client. Reserve `MangadexPluginBundle.Instance` for call sites that actually need the aggregated bundle.

## Code Consolidation & Reusable Patterns

This plugin now implements reusable SDK patterns directly in both transports.
Use this as the baseline for new providers.

### 1. URL Building: Single Source of Truth

`Core/MangadexProviderClient.cs` is the single source of truth for provider URLs,
and `Core/MangadexPluginBundle.cs` makes that ownership explicit.

Do not duplicate URL strategy logic in multiple files.

### 2. Batch Metadata Loading: PluginBatchMetadataLoader

For statistics metadata, use `PluginBatchMetadataLoader<T>` instead of writing
manual chunking + fallback loops.

Current usage:
- `Infrastructure/WasmClient.cs`
- `Services/AspNetClient.cs`

This gives consistent batch behavior and automatic per-item fallback.

### 3. Chapter Feed Pagination: PluginWasmPagingJsonHelpers

For multi-page chapter feeds, use `PluginWasmPagingJsonHelpers.MergeChapterFeedPages`
instead of custom offset loops.

Current usage:
- `WASM/WasmClient.cs`
- `WASM/WasmGlue.cs`

### 4. Search Query Enrichment: PluginSearchQueryEnricher

`Infrastructure/ProviderSearchQueryResolver.cs` inherits from
`PluginSearchQueryEnricher` for provider-specific filter resolution and caching.

## Adding a New Provider Plugin

When creating a new plugin, follow this checklist:

1. **Create CoreClient**: Transport-agnostic domain logic calling your provider API.
2. **Reuse SDK patterns**:
   - Use `PluginBatchMetadataLoader<T>` for batch metadata with fallback.
   - Use `PluginWasmPagingJsonHelpers.MergeChapterFeedPages()` for pagination.
   - Inherit from `PluginSearchQueryEnricher` for search enrichment.
3. **Single URL strategy**: Keep one URL strategy implementation in your provider client.
4. **Bundle provider pieces explicitly**: Group your provider client, query enricher,
   and suggestion provider behind one provider bundle.
5. **Keep transport adapters thin**: Put provider/domain behavior in `CoreClient`.

## Where to Start

### 1. Pick a transport

- **ASP.NET**: Good for desktop/server plugins. Full .NET ecosystem, simpler debugging.
  Edit `Services/AspNetClient.cs` and `Infrastructure/CoreClient.cs`.

- **WASM**: Good for mobile/web-embedded plugins. Smaller footprint, sandboxed execution.
  Edit `Infrastructure/WasmClient.cs` and `Infrastructure/CoreClient.cs`.

### 2. Implement domain logic

Open `Infrastructure/CoreClient.cs` and replace the Mangadex calls with your provider's API:
- `SearchFromPayload()`: Parse search results from your provider
- `GetChaptersFromPayload()`: Parse chapters/episodes
- `GetPageFromPayload()`: Retrieve page/segment data

### 3. Update provider URLs

Edit `Core/MangadexProviderClient.cs` to point to your API endpoints.
Edit `Infrastructure/ProviderSearchQueryResolver.cs` if your search API has different query syntax.

### 4. Update manifest

Edit `EMMA.TestPlugin.plugin.json`:
- Set `id`, `name`, `version`
- Update `capabilities.domains` to your provider's domains
- Adjust `cpuBudgetMs` and `memoryMB` based on your expected workload

### 5. Test locally

```bash
dotnet build
dotnet run --project EMMA.TestPlugin.csproj
```

Check the host can handshake and your search operation responds.

### 6. Sign and package

See the "Signing" section below for delegated RSA key setup, then:

```bash
./scripts/build-pack-plugin.sh EMMA.TestPlugin.plugin.json
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

## Notes on Example Behavior

This plugin lacks production features like retry policies and caching. These are
intentionally omitted to keep the example focused on the EMMA SDK integration
pattern. For a real plugin:

- Add retry/exponential backoff for network failures
- Implement local or distributed caching for search results and chapter lists
- Handle rate limiting and provider-specific error responses
- Add per-request timeouts aligned with your capability budget