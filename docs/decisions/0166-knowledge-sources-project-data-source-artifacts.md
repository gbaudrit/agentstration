# ADR-0166: Knowledge Sources project Data Source artifacts

## Status

Accepted

## Context

ADR-0163 moved governed origin acquisition to Data Sources, but Knowledge Sources still retained their earlier ingestion-oriented shape. That leaves two authorities for acquisition and cannot express the intended cases where one acquired origin feeds several Knowledge views or one Knowledge view aggregates several origins.

The repository has not shipped the Knowledge Source Profile and direct Knowledge acquisition model. A compatibility migration for those pre-release resources would preserve the wrong ownership boundary and add behavior that no supported installation requires.

## Decision

A Workspace-owned `KnowledgeSource` defines a projection over one to thirty-two visible Data Sources. Each named binding references an instance-, tenant-, or Workspace-scoped Data Source through the existing ancestor-only visibility rules. A binding may select a published Flow with `flow.contract: artifact.transform/v1`; omitting it is an identity transformation that passes the acquired Artifact references unchanged.

The Knowledge Source selects one published projection Flow with `flow.contract: knowledge.projection/v1` and one retrieval Flow with `flow.contract: knowledge.retrieval/v1`. Projection resolves either an explicitly selected successful acquisition or the latest successful acquisition for each binding, pins the exact Data Source scope, UID, generation, acquisition, acquisition composition, acquired Artifacts, optional transformation Flow and FlowRun, and then invokes the projection Flow once with all prepared inputs.

The built-in `knowledge-projection-builtin` Flow is an identity aggregation: it publishes the prepared Artifact set without interpreting or normalizing heterogeneous formats. A specialized projection Flow may normalize, merge, split, index, or otherwise reshape that set. There is no built-in transformation Flow.

Only durable publishable Artifacts emitted by the projection Flow enter the immutable Knowledge Snapshot. Acquired and transformed inputs remain provenance unless the projection emits them. The Snapshot pins the exact projection FlowRun, retrieval Flow, Data Source inputs, acquisition composition, optional transformations, output Artifact receipts, storage lineage, and integrity evidence. A successful projection atomically publishes and activates the Snapshot; a failed projection preserves the previously active Snapshot.

`KnowledgeSourceProfile`, `knowledge.ingestion/v1`, and Knowledge-owned origin configuration are superseded for new Knowledge composition. No historical compatibility or record-rewrite requirement is introduced because this model has not shipped from the integration branch. Data Source Profiles remain the reusable governance and acquisition boundary.

This decision supersedes the Knowledge-owned acquisition and Knowledge Source Profile decisions in ADR-0148, ADR-0151, ADR-0158, ADR-0161, and ADR-0162. It also supersedes ADR-0163's temporary compatibility path. Their historical rationale remains recorded; ADR-0152, ADR-0153, ADR-0154, ADR-0163's Data Source ownership, and ADR-0165's Flow metadata convention remain in force.

## Consequences

- One Data Source can feed several Knowledge Sources, and one Knowledge Source can aggregate several Data Sources.
- Acquisition policy, credentials, network access, Tools, and provider bindings remain entirely outside Knowledge.
- Projection history is reproducible from exact immutable input and execution evidence.
- Heterogeneous Snapshots are valid; normalization is an explicit projection concern rather than an implicit platform rule.
- Pre-release Knowledge acquisition resources and bootstrap compositions must be recreated against Data Sources and projection Flows rather than migrated.
