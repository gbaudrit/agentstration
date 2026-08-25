# Development slots

A development slot is an isolated local Agentstration execution associated implicitly with a Git worktree. It lets multiple branches run through the single `src/Agentstration.AppHost` topology at the same time without sharing Agentstration data or fixed application ports.

From any Agentstration worktree, run:

```powershell
./dev/run.ps1
```

The script uses the current branch as the slot identifier, lowercases it, and replaces separators or unsupported characters with hyphens. For example, `codex/tools` becomes `codex-tools`; a regular checkout of `main` uses `main`. A detached worktree uses `detached-<commit>`.

The AppHost receives the slot identifier and a worktree-local data root:

```text
.agentstration/slots/<slot>/
```

This directory contains the JSON content store, Management, Identity, Runtime, Work, Flow and scheduler SQLite databases, data-protection keys, local secrets, Pack artifacts and Work artifacts. `.agentstration/` is ignored by Git. Because the root is inside the worktree, two worktrees do not share these files even if their normalized slot identifiers happen to match.

Aspire continues to allocate the Console, Workplace and extension endpoints dynamically. The launch script also obtains available ports for the AppHost dashboard, telemetry receiver and resource service instead of using the fixed development launch profile, so multiple AppHosts can coexist.

Once Aspire prints its authenticated dashboard URL, the script opens it in the default browser. Use `./dev/run.ps1 -NoBrowser` to keep the launch terminal-only.

Ollama, llama.cpp and LocalAI inference servers are deliberately not duplicated. Each slot's AEP extension process consumes the external endpoint already configured for that provider.

To display all worktrees known to Git and their inferred slots:

```powershell
./dev/slots.ps1
```

The first version reports `SLOT`, `BRANCH` and `WORKTREE`; it does not track whether a slot is currently running. Stop a slot with `Ctrl+C` in the terminal where it was launched. Slot data is never removed automatically.

For example:

```powershell
git worktree add ../agentstration-slots/codex-tools codex/tools
Set-Location ../agentstration-slots/codex-tools
./dev/run.ps1
```
