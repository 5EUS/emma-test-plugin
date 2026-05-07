---
description: "Use when editing plugin.dev.json, plugin.dev.sample.json, or AI-driven plugin development config for EMMA CLI workflows."
applyTo: "plugin.dev*.json"
---

- Keep profile names aligned with the CLI conventions: `wasm-dev`, `linux-dev`, and `windows-dev` unless there is a deliberate reason to diverge.
- If the repo uses `plugin.dev.sample.json`, remember the CLI will only load it when `EMMA_PLUGIN_DEV_CONFIG` points to it.
- Per-profile `sync.enabled` must be `true` or build/watch sync will be skipped even if `destinationPath` is present.
- Keep `destinationPath` values concrete enough for local agent workflows and prefer paths that mirror the target runtime layout used by `emmaui`.
- Watch globs should cover the actual transport-specific source folders and the config file itself when the workflow depends on live watch/reload.