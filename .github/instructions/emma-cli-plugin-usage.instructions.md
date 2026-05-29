---
description: "Use when working on plugin build/test/dev-loop tasks through EMMA.Cli, especially session bootstrap, profile selection, doctor/build/watch/scenario flows, or plugin.dev configuration."
applyTo: "**"
---

- Prefer the normalized EMMA.Cli plugin-dev commands (`session`, `doctor`, `build`, `pack`, `watch`, `scenario`, `serve`) before repo-specific helper scripts when the goal is local plugin build, runtime validation, or troubleshooting.
- Start plugin workflow investigation with `session` or `doctor` so the active profile, inferred targets, artifact paths, and pre-launch diagnostics are explicit before changing code or config.
- Keep `EMMA_PLUGIN_PROFILE` explicit whenever the task depends on a specific runtime target such as `wasm-dev`, `linux-dev`, or `windows-dev`, especially across multi-step build/test flows.
- If the workflow uses anything other than the auto-discovered `plugin.dev.json`, set `EMMA_PLUGIN_DEV_CONFIG` explicitly; `plugin.dev.sample.json` is only used when pointed to directly.
- Treat `build` as the canonical artifact-producing step before `scenario`, `watch`, or host-side validation. For `wasm-dev`, source edits do not change the runtime artifact until `build` runs.
- When watch or sync behavior matters, verify the selected profile's `watchGlobs` cover the changed files and that `sync.enabled` is `true` when artifact mirroring into `emmaui` is expected.
- Prefer `scenario paged-smoke` or repo-defined scenarios for quick functional checks before broader manual testing, and use `serve` when the local session UI/API helps inspect profile state, watch status, or diagnostics.