---
description: "Workspace-wide production rules for EMMA backend, emmaui frontend, and related plugin repos."
applyTo: "**"
---

- Treat this workspace as a production-only multimedia aggregation codebase.
- EMMA is the core backend, emmaui is the frontend, and the plugin repositories are part of the same system.
- Design for the current media types first, but keep the architecture ready for audio, text-based paged media, and other future media types.
- Keep changes clean, pragmatic, and production-ready.
- Prefer small, focused files and components; avoid very large files, especially 1000+ line UI files.
- Use clear abstractions and proven design patterns, but do not over-engineer.
- Preserve the existing architecture unless there is a clear improvement.
- Avoid throwaway, prototype, or placeholder code unless it is explicitly requested.
- Ask before using terminal tools unless the user explicitly asks for terminal use.