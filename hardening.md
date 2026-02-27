# EMMA.TestPlugin Hardening Notes

## Current state (from debugging)

- The Flutter app calls native FFI, and native currently expects a PluginHost process to already be running on `localhost:5223`.
- `EMMA.TestPlugin` currently accepts multiple port sources (`EMMA_PLUGIN_PORT`, `EMMA_TEST_PLUGIN_PORT`, `--port`, default), which increases configuration attack surface.
- Plugin manifests are discovered and used for capability/policy metadata, but domain permissions are **not** currently a strict host-enforced egress firewall for plugin-originated HTTP calls.
- On macOS, PluginHost currently applies no additional host-side sandboxing (`MacOsPluginSandboxManager` is effectively pass-through).

## Key risk clarified

Even if a manifest declares:

```json
"permissions": { "domains": ["api.mangadex.org"] }
```

that does **not** currently guarantee all outbound plugin HTTP is blocked for non-listed domains. Plugin code can still select arbitrary base URLs unless enforcement is implemented in code or infrastructure.

## Plugin trust model (arbitrary code execution)

- Plugins should be treated as potentially arbitrary code execution within the plugin process boundary.
- Current controls mostly constrain startup/communication conventions; they do not yet provide a full runtime jail on all platforms.
- Therefore, plugin packages must be considered high-risk inputs unless additional isolation is enforced.

Implications:

- A malicious plugin can attempt unexpected network/file/process operations permitted by the host OS/runtime context.
- Manifest-declared capabilities/permissions should be treated as policy intent, not absolute enforcement, until host-level enforcement is complete.

## High-priority hardening actions

1. **Host-controlled runtime config only**
	- Prefer `EMMA_PLUGIN_PORT` as the only port source in production.
	- Keep `EMMA_TEST_PLUGIN_PORT` / `--port` only for local dev (guarded by environment).

2. **Reduce exposed surfaces**
	- Remove non-essential HTTP endpoints from plugin process (for example `/` health text endpoint) and expose only required gRPC services.

3. **Enforce outbound domain allowlist**
	- Add explicit allowlist checks in plugin HTTP client code before requests, using manifest-declared domains.
	- Longer-term: route plugin outbound HTTP via PluginHost egress proxy and enforce `permissions.domains` centrally.

4. **Require plugin identity/auth on gRPC**
	- Correlation IDs are not authentication.
	- Add host-issued shared secret/token metadata and validate it in plugin RPC guard.

5. **Fail closed, not silently**
	- Avoid fallback behavior that masks transport/security failures as empty results.
	- Return explicit errors when PluginHost/plugin endpoint is unavailable.

6. **Tighten plugin-declared permissions**
	- Keep only required domains (`api.mangadex.org`, `uploads.mangadex.org`).
	- Remove path permissions unless plugin truly requires filesystem access.

7. **Signature and supply-chain controls**
	- Move toward `RequireSignedPlugins=true` in environments beyond local dev.
	- Treat signature validation failures as hard startup failures for plugins.

## Operational guidance

- Ensure PluginHost startup is deterministic (absolute paths for project/manifest/sandbox) to avoid accidental downtime and misleading connection errors.
- Validate with explicit checks:
  - `GET /plugins/available`
  - `POST /plugins/rescan`
  - `GET /pipeline/paged/search?pluginId=...&query=...`

## Proposed implementation order

1. Lock production port source to `EMMA_PLUGIN_PORT`.
2. Remove plugin non-gRPC endpoints.
3. Add plugin-side domain allowlist enforcement.
4. Add RPC auth metadata validation.
5. Enable strict signed-plugin policy outside dev.


### Checklist

