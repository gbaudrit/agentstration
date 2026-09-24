# ADR-0143: Instance initialization uses a durable fenced lease

Status: Accepted — 2026-09-24

## Context

Agentstration performs storage preparation, identity bootstrap, declarative bootstrap, extension discovery, and built-in Workspace resource projection before serving requests. Storage readiness was process-local, while built-in Tool resources were also projected from Tool catalog read endpoints. Concurrent catalog reads on a fresh Workspace could both observe a missing resource and attempt the same insert. Separate authoritative server processes had no durable application-level signal identifying an initialization owner or a completed initialization version.

A persisted `Initializing` flag alone is insufficient. Two processes can claim it concurrently, a terminated owner can leave it stuck, and a delayed former owner can overwrite the result of a newer attempt.

## Decision

Application initialization is coordinated by one instance-scoped `InstanceInitialization` Management resource. Its desired target version, status, owner instance identifier, lease expiry, monotonically increasing fencing token, bounded failure detail, and update time are durable.

Acquisition uses the Management store's exact-scope create and ETag compare-and-swap operations. A process may acquire an absent, failed, older-version, or expired record. The owner renews its lease while it performs application bootstrap. Completion and failure require the same owner identifier and fencing token. Another process waits while a valid lease exists, may take over an expired lease with a higher fencing token, and reports ready locally only after the current target version is durably `Ready`.

Storage schema preparation remains outside this coordinator. PostgreSQL retains its provider-specific advisory migration lock. Creation of the instance resource scope is made atomic in both Management stores so the coordinator can safely start after storage preparation.

The coordinated work includes official instance resources, Development identity bootstrap when selected, extension discovery, declarative bootstrap, and built-in resource reconciliation for every active Workspace. Newly created Workspaces begin in `Initializing`, receive the same built-in resource provisioning, and become `Active` only after it succeeds. An interrupted Workspace remains unavailable and is recovered by a later coordinated startup.

Tool and Tool Provider list operations are read-only. Built-in Tool projection remains idempotent and handles an exact-identity create race by accepting it only when that exact scoped resource can subsequently be read and validated.

Liveness remains independent from initialization. Application readiness is reported from the instance initialization coordinator rather than storage readiness alone.

## Consequences

- Concurrent server startup executes each application initialization target version once.
- Expired ownership is recoverable and stale owners cannot complete after takeover.
- Raising the explicit target version reruns application initialization after an upgrade.
- A failed initialization retains bounded diagnostics and keeps readiness false.
- Existing and newly created Workspaces receive built-in Runtime Profile, Tool Provider, Tool, and ToolCategory resources without read-side mutations.
- The initialization resource uses the existing provider-neutral Management resource concurrency boundary and requires no new persistence table.
- This decision coordinates startup only. It does not make queues, schedulers, file-backed state, Data Protection, or other runtime facilities generally safe for horizontally scaled operation; those constraints remain governed by ADR-0078 and later subsystem decisions.
