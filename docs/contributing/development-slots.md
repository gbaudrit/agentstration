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

With the optional PostgreSQL profile, file-backed state remains in this directory but relational data uses the Docker volume `agentstration-<slot>-<instance-id>-postgresql`. The AppHost creates the random instance identifier once in `.agentstration/instance-id`, independently of the branch-derived slot. It survives restarts, branch changes, and a move of the complete worktree directory, while a newly created worktree receives a different identity even when it uses the same slot. `Agentstration:InstanceId` may explicitly restore or override the identity. A native volume is required because PostgreSQL initialization changes Unix ownership and permissions; a bind mount into a Windows worktree fails under the Linux/WSL Docker daemon.

Docker volumes are host-global, so the persisted worktree identity is part of the volume name. The AppHost password is generated and persisted under an identity-specific user-secrets parameter. Keep `.agentstration/instance-id`, its matching secret, and its volumes together. Deleting the identity file does not delete existing volumes, but the next launch creates a new identity and no longer discovers them automatically.

Aspire continues to allocate the Console, Workplace and extension endpoints dynamically. The launch script also obtains available ports for the AppHost dashboard, telemetry receiver and resource service instead of using the fixed development launch profile, so multiple AppHosts can coexist.

Once Aspire prints its authenticated dashboard URL, the script opens it in the default browser. Use `./dev/run.ps1 -NoBrowser` to keep the launch terminal-only.

Ollama, llama.cpp and LocalAI inference servers are deliberately not duplicated. Each slot's AEP extension process consumes the external endpoint already configured for that provider.

To display all worktrees known to Git and their inferred slots:

```powershell
./dev/slots.ps1
```

The command reports `SLOT`, `INSTANCE`, `BRANCH` and `WORKTREE`; `INSTANCE` is `-` until the AppHost has created that worktree's identity. It does not track whether a slot is currently running. Stop a slot with `Ctrl+C` in the terminal where it was launched. Slot data and PostgreSQL volumes are never removed automatically. Use the reported identity to inspect `agentstration-<slot>-<instance-id>-postgresql`; removing that volume permanently deletes the slot's relational data.

For example:

```powershell
git worktree add ../agentstration-slots/codex-tools codex/tools
Set-Location ../agentstration-slots/codex-tools
./dev/run.ps1
```
