# MangaDex plugin (Rust, `emma:core` Wasm component)

A real EMMA plugin that browses and reads manga from the live MangaDex API. It is the
Rust counterpart of the C# plugin in the repository root: same behavior, but it compiles
straight to a WebAssembly component (`wasm32-wasip2`), so it needs no LLVM, wasi-sdk or
Docker.

It exports `emma:core/meta`, `catalog` and `assets` (plus the optional `search-assist`
interface for title suggestions and rating/follow enrichment) and imports `emma:core/host`
for HTTP, cache, logging and settings.

## Node model

| Kind            | Id                    | Notes                                         |
|-----------------|-----------------------|-----------------------------------------------|
| `manga`         | `manga:<uuid>`        | search results and popular list               |
| `manga.chapter` | `chapter:<uuid>`      | children of a manga; chapters with no hosted pages or an `externalUrl` are skipped |
| `manga.page`    | `page:<chapter>:<n>`  | children of a chapter; `assets.resolve` returns the image URL |

## Wire contract

`wit/` is a vendored copy of `src/EMMA.Contracts/wit` from the EMMA repository (branch
`rewrite`, phase 5). It is the contract, so do not edit it here: re-copy it when EMMA changes
the contract. `src/bindings.generated.rs` is produced from it by `wit-bindgen` and is
excluded from lints.

## Build and test

```sh
rustup target add wasm32-wasip2
cargo test --locked                # mapping and helper unit tests (host target, no network)
cargo clippy --locked --all-targets -- -D warnings
sh package.sh                      # builds and writes rust/dist/
```

`package.sh` produces an **unsigned development package**
(`dist/emma.plugin.mangadex-<version>.zip` holding `plugin.json` and `plugin.wasm`). EMMA
installs it only when configured to allow unsigned packages. Granted permissions: network
`api.mangadex.org`, `uploads.mangadex.org` and `*.mangadex.network`, plus the host cache.

## Settings

Declared through `meta.describe`, so the host builds the settings screen, in three
categories: connection (`connection.username`, `connection.apiToken`), search defaults
(`search.defaultLanguage`, `search.defaultMaturity`) and advanced
(`advanced.enableVerboseLogging`, `advanced.requestTimeout`, `advanced.apiBaseUrl`). See
`src/component.rs` (`declared_settings`).
