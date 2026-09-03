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

With the default SQLite profile, this directory contains the Management, Identity, Runtime, Work, Flow and scheduler databases, data-protection keys, local secrets, Pack artifacts and Work artifacts. `.agentstration/` is ignored by Git. Because the root is inside the worktree, two worktrees do not share these files even if their normalized slot identifiers happen to match.

With the optional PostgreSQL profile, file-backed state remains in this directory but relational data uses the Docker volume `agentstration-<slot>-postgresql`. A native volume is required because PostgreSQL initialization changes Unix ownership and permissions; a bind mount into a Windows worktree fails under the Linux/WSL Docker daemon. Docker volumes are host-global, so explicitly overridden slot names must also be unique across concurrently running worktrees. The AppHost password is generated and persisted in user-secrets; keep that secret and its volume together.

Aspire continues to allocate the Console, Workplace and extension endpoints dynamically. The launch script also obtains available ports for the AppHost dashboard, telemetry receiver and resource service instead of using the fixed development launch profile, so multiple AppHosts can coexist.

Once Aspire prints its authenticated dashboard URL, the script opens it in the default browser. Use `./dev/run.ps1 -NoBrowser` to keep the launch terminal-only.

Ollama, llama.cpp and LocalAI inference servers are deliberately not duplicated. Each slot's AEP extension process consumes the external endpoint already configured for that provider.

To display all worktrees known to Git and their inferred slots:

```powershell
./dev/slots.ps1
```

The first version reports `SLOT`, `BRANCH` and `WORKTREE`; it does not track whether a slot is currently running. Stop a slot with `Ctrl+C` in the terminal where it was launched. Slot data and PostgreSQL volumes are never removed automatically. Inspect a PostgreSQL volume with `docker volume inspect agentstration-<slot>-postgresql`; removing it permanently deletes that slot's relational data.

For example:

```powershell
git worktree add ../agentstration-slots/codex-tools codex/tools
Set-Location ../agentstration-slots/codex-tools
./dev/run.ps1
```
