# ADR-0156: Workspaces reconcile code-owned built-in resources

## Status

Accepted

## Context

Agentstration requires a small set of platform-owned resources in every Workspace, independently of optional declarative Bootstrap profiles. Artifact staging already projected internal Tools, ToolSets, a binding, and Storage Flows, but single-Workspace creation and startup-wide reconciliation did not execute the same operation. Automatically provisioned resources also mixed unsuffixed names with the `maf-builtin` convention.

Knowledge Sources require compatible published ingestion and retrieval Flows before they can be enabled. Shipping those Flows only in the Development Bootstrap catalog would make the executable default depend on a profile and would omit Workspaces created after startup. Creating schema-only Flows would expose selectable resources that cannot execute.

## Decision

`WorkspacePlatformResourceProvisioner.EnsureAsync` is the complete idempotent reconciliation boundary for one Workspace. Interactive creation, identity Bootstrap handlers, local initialization, and startup-wide reconciliation all delegate to it. `EnsureAllAsync` only enumerates eligible Workspaces and calls that boundary.

Agentstration provisions code-owned Artifact and Knowledge implementations independently of declarative Bootstrap. Replaceable platform resources use a `-builtin` suffix and the `agentstration.io/builtin: "true"` annotation. Protocol contracts and unit Tool capability identifiers remain invariant and are not suffixed.

The built-in Knowledge ingestion Flow routes `knowledge.ingestion/v1` through an exact published ToolSet version that imports existing governed durable Artifacts. The built-in retrieval Flow branches on the trusted operation and routes `knowledge.search/v1`, `knowledge.query/v1`, and `knowledge.read/v1` through an exact published ToolSet version. Its local implementation performs bounded deterministic UTF-8 retrieval over filesystem-backed Snapshot Artifacts without a model or network dependency. Other providers remain available through operator-authored ToolSets and Flows.

Legacy built-in Artifact resources remain readable so persisted references continue to resolve. Reconciliation demotes the legacy built-in default staging binding and creates or promotes the suffixed binding, avoiding two active defaults. New Workspaces receive only the suffixed identities.

## Consequences

- A Workspace has the same built-in resources immediately regardless of its creation path or Bootstrap configuration.
- Startup reconciliation is retry-safe and does not contain capabilities absent from single-Workspace provisioning.
- Built-in origin is both human-readable and machine-detectable; the annotation remains authoritative for reconciliation.
- Standard Knowledge Flows are executable local defaults, not mandatory architecture. A Knowledge Source may bind any compatible published Flow.
- Existing staged Artifacts and published Flow history keep resolving through retained legacy resources during the compatibility period.
- Built-in retrieval deliberately supports bounded textual filesystem content. Binary formats, remote stores, semantic search, indexes, crawlers, and model-backed answers require alternative governed Flow compositions.
