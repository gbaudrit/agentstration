# ADR-0270: Coding CLI Runs use AWP and an isolated native helper

## Status

Accepted — implementation for [#966](https://github.com/gbaudrit/agentstration/issues/966).

## Context

A local native Coding CLI must execute pinned functional work without becoming another scheduler,
accessing server databases or receiving the authoritative server's repository credentials. ADR-0168
defines AWP placement, workload identity, leases, fencing and explicit worker-loss failure. ADR-0268
defines portable native HarnessProfile files; ADR-0269 retains functional attempt evidence.

## Decision

Add a separate Coding worker executable advertising the versioned `coding-cli` runtime capability.
Extract the existing client, credential enrollment, assignment session, heartbeat/terminal runner and
registration loop into the actual shared Worker Core implementation. The MAF executable keeps its
runtime executor and capabilities. Both executors reuse the same protocol lifecycle; neither worker
references Infrastructure, server storage or EF Core. Shared lifecycle tracing uses
`Agentstration.Runtime.Worker` with explicit runtime/capability/material-version tags; MAF's own
execution telemetry remains separate.

A provider-neutral Runtime Flow delegates one complete root Run to an explicitly compatible
capability/version/material version, with bounded object configuration. Its owning capability
resolver validates and pins configuration at publication. Flow Core does not depend on Coding,
Runtime implementations or storage. Existing root Flow submission, WorkPlan admission, assignment
claim and functional task identities remain authoritative. No active execution tree is split across
workers and no worker-loss recovery is introduced.

The first native configuration uses the Codex materializer. Coding owns exact HarnessProfile and
installed RuntimeProfile resolution, Project/repository authorization and native execution material.
Workers receive bounded immutable configuration and the attempt/plan/task/Run coordinates needed
for evidence. They do not receive a resource repository, management API credentials or a database
path. The pure materializer is shared through Coding Native, without an Application dependency.

The initial checkout source is an explicit operator-maintained local repository cache. Worker
configuration maps an authorized native repository identity and canonical origin to that cache;
request material selects an exact commit, never a local path. Origin and commit must match before
checkout. There is no hidden remote fetch or checkout credential relay. A task obtains one isolated
repository, with actual native file ownership and content verified before materialization.

Linux Bubblewrap provides the first supported native process isolation. A separate helper mode runs
inside an empty filesystem namespace with curated read-only executable/system roots, the approved
read-only cache and size-bounded temporary writable filesystems. The parent AWP worker and its
credential files remain outside the helper's mounts. The helper does not initialize AWP credentials.
Missing required isolation fails closed; it does not select an unrestricted process fallback.

Native execution and repository checks cannot modify the checkout's Git control directory. All
post-execution Git operations also remain inside isolation: repository-controlled filters or config
must never execute with the parent worker's filesystem authority. Only bounded result, patch, test
and consumption observations leave the helper. Native account authentication, when explicitly
configured by the operator (static API-key authentication in this version; OAuth refresh tokens are rejected), is exposed only to the selected native invocation; untrusted repository
checks do not receive it. Native provider authentication is distinct from server Secret resolution.

This explicitly extends ADR-0168's data-plane boundary for an optional native CLI runtime: inference
uses the operator's native CLI account/configuration. The MAF path continues to invoke governed
server model and Tool operations. Ordinary deterministic local Work does not require the CLI, a
provider account, Bubblewrap or a remote model. Native execution does not become an Agent deployment.

Native JSONL usage observations retain stable invocation and assignment/task-attempt coordinates.
Known, partial and unavailable counters remain distinguishable, including failure and cancellation.
No cost, token total or functional acceptance is inferred from missing reporting. Cancellation kills
the isolated process tree; fenced AWP ownership continues to control every accepted event and
terminal mutation. Code publication, shared accounting and issue acceptance remain later consumers.

## Consequences

The native worker has explicit platform, isolation, installed CLI, repository cache and optional
native account prerequisites. Enrollment and AWP semantics stay shared. A new capability cannot
silently consume MAF work or unsupported material. Tests use deterministic fake CLI processes,
real AWP envelopes and native isolation checks; optional vendor inference remains separately
reported. Workspace seeding must diagnose installed prerequisites rather than create Instance
providers or install binaries. Broader runtime selection and cross-worker review remain outside
this first local Coding capability.
