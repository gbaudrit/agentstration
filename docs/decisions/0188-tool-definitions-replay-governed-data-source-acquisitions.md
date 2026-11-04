# ADR-0188 — ToolDefinitions replay governed Data Source acquisitions

## Status

Proposed

## Context

An acquisition already records its Data Source, target, parameters, resolved Flow, and artifact manifest. A consumer may need to fetch a fresh artifact using the same settings, including from a Flow that consumes the artifact in a subsequent step.

## Decision

- An acquisition-backed ToolDefinition lives in the Workspace that ran the successful acquisition. For administrative contexts, this is the operational Workspace defined in ADR-0185.
- The ToolDefinition references the successful acquisition in that Workspace. It exposes an empty input object, and its backing acquisition supplies the saved target and parameters. Callers cannot override them.
- Each distinct call starts a governed acquisition. Admission checks the current Data Source and its profile, rejects a stale target, and verifies that the original target still identifies the same selection. The new acquisition records its own resolved dependencies and Flow version. A retry with the same call identity uses the existing acquisition idempotency contract.
- The Tool waits for completion within its invocation timeout. It returns the reference to exactly one selected publishable artifact and the new acquisition ID. The artifact selection uses its kind and, when present, its name. A missing, ambiguous, unreadable, or expired result fails the invocation.
- The ToolDefinition uses the existing Tool projection and execution pipeline. The Data Sources service owns replay admission and acquisition state, including source authorization. The Artifact service owns access to the output. The recorded Flow reference on the ToolDefinition describes the original acquisition.

## Consequences

- A caller passes the returned reference to a consumer that understands staged or durable artifacts. Workspace isolation and artifact retention apply to both the acquisition and the resulting reference.
- To use the Tool from a business Workspace, run the original acquisition in that Workspace and create the Tool there. An administrative acquisition belongs to its dedicated operational Workspace.
- A worker that waits for the Tool inside a Flow or Agent execution needs another available assignment slot to run the acquisition, as with other synchronous calls to worker-backed Tools. For a single nested invocation, Aspire can supply that slot through `Agentstration:RuntimeWorkers:Count=2`. A standalone worker can use `Agentstration:RuntimeWorker:MaximumConcurrentAssignments=2`.
