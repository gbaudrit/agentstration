# ADR-0244: Agent deployments record exact durable assignments

## Status

Proposed

## Context

A definition graph describes an Agent's draft or retained publications and their
resource dependencies. It cannot establish which revision is assigned to an
environment, nor where that revision actually executed. Several deployments can
use the same revision, and an assignment must exist without an active Run.

## Decision

Extend the Agents-owned AgentDeployment resource with a stable UID and an ordered
history of immutable assignment values. Each value records its own configuration
UID, exact Agent/revision UIDs, revision owner/namespace/name/version/hash,
environment, logical target identity/kind/name, declared runtime and recording
time. Reassignment atomically appends a value and changes the current assignment
under the resource's existing ETag compare-and-swap. History is bounded to 64
values; exhaustion requires an explicit new deployment. No values are silently
pruned. Generic JSON persistence needs no relational migration.

The Agents service and resource-authorized endpoints own creation, reassignment
and metadata projection. Only Workspace assignments are writable. Definition and
assignment selectors expose authorized metadata, never instructions, credentials
or execution material. Every configuration additionally requires Discover and
binding inspection of its exact retained revision. Missing/purged/revoked revision
metadata makes that configuration unavailable, rather than resolving a homonym.
History availability therefore depends on revision retention and authorization.
Legacy name-only deployments acquire no inferred assignment or history.

Assignment records desired intent. Saving one neither provisions infrastructure,
starts work, routes a Run to a target, nor changes publication/adoption policy.
The deployment reconciler acknowledges the recorded configuration while leaving
operational placement unavailable. Existing legacy deployment behavior remains.

A Runtime request may explicitly supply an exact deployment UID/configuration UID
and owning address. The Agent resolver checks those values against the resolved
immutable revision, including owner, namespace, UID and definition hash. The Run
service persists the validated identity with the Run. Retained historical
configurations remain addressable, so reassignment does not rewrite prior Runs.
Requests without this reference retain their existing behavior. No automatic
association by revision, name, timing or current deployment pointer is introduced.
SQLite and PostgreSQL filter this identity before applying the Run sample limit.

SystemGraph carries neutral assignment metadata and typed configuration/revision
relations, with repeated binding authorization in strict and progressive reads.
The operational overlay independently selects exact recorded Run associations and
native assignment/attempt/Worker-session evidence. Legacy Runs and Flow bindings
are not retrospectively attributed. Worker details retain their existing separate
authorization requirement. Unknown evidence never becomes zero or healthy.

The Console offers definition (draft, retained revision, two selected revisions)
and deployment (selected configuration, desired, observed) perspectives. Definition
comparison merges independently dated authorized observations, remains partial,
and rejects conflicting metadata for the same graph identity. Selecting an Agent
lists its authorized assignments across deployment namespaces and uses the selected
deployment’s owning address for graph reads and reassignment. An empty deployment
selection explains the gap and links to Definition and authorized Workspaces,
without falling back to the Agent dependency graph or creating an assignment. Rendering may group
current and pinned generations of the same UID, while inspection/export retain
all exact semantic identities and evidence.

Deployment rendering uses native LikeC4 deployment nodes, container instances and
`deploymentView`. The environment contains a declared logical target for desired
intent, or attested Worker/session nodes for observed placement, then an instance
of the runtime container. The Agent revision is a logical component of that
container, not an invented independently deployed C4 container. Authored labels
never become builder expressions. Observed views show at most 20 distinct sessions
from the bounded dated sample; physical hosts and target-pool membership remain
unknown without evidence. No evidence suppresses the diagram and states the gap.
Renderer IDs, placement layout and visual grouping stay outside neutral contracts.

## Consequences

Assignments can be inspected without active Runs, multiple revisions/deployments
remain distinct, and retargeting is reviewable without inventing execution proof.
This increment deliberately does not implement infrastructure provisioning,
scheduling, rollout/traffic policy, automatic revision adoption, revision-retention
protection, or physical infrastructure discovery. Recorded Runs alone cannot prove
that a Worker belonged to the declared target. UI journeys remain deferred under
#1201/#1203 until explicit maintainer UX validation and the owning PR's merge.
