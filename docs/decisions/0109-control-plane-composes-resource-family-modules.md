# ADR-0109 — The Control Plane composes plural resource-family modules

## Status

Accepted

## Context

`Management` has been used both for the architectural Control Plane and for a concrete project family. That made every declarative resource appear to belong to one business domain, even when Agents, Flows, Triggers, Models, Tools, Secrets, Sources, Packs, Extensions, Identity, Runtime, and Work have different invariants and execution lifecycles.

Renaming the catch-all module to `ControlPlane` would preserve the same coupling. Creating an `Abstractions + Core + Contracts + Storage` stack for every resource would instead make the project graph mechanical and expensive.

## Decision

The **Control Plane** is an architectural umbrella and API façade. It is not a resource owner and there is no `Agentstration.ControlPlane` business module.

Shared responsibilities are split narrowly:

- `Agentstration.Resources` owns the provider-neutral resource model: canonical identity and addressing, metadata, namespaces, ownership-scope references, generations, versions, status and references.
- `Agentstration.ResourceManagement` owns generic resource use cases and ports: CRUD concurrency, common scope resolution and policy, validation orchestration, publication, manifest import/export and lifecycle operations.
- `Agentstration.ResourceManagement.Storage.*` implements generic resource persistence. A physical SQLite or PostgreSQL database may still host tables from several families, but lifecycle-specific ports remain owned by their family.

Business ownership uses plural family names. Additional assemblies exist only where they enforce a compilation, contract, provider or persistence boundary. The complete ownership map for existing managed kinds is:

| Family | Managed resource kinds and lifecycle state |
| --- | --- |
| Agents | `Agent`, `AgentRevision`, `AgentDeployment`; revision publication, deployment reconciliation and provisioning |
| Triggers | `Trigger`; `TriggerOccurrence`, scheduling, manual firing, idempotency and history are Trigger lifecycle state even though occurrences are not generic resources |
| Flows | `Flow`, immutable `FlowVersion`, `FlowRun`, events, recovery and causality |
| Models | `ModelProvider`, `ModelProfile` |
| Runtime | `RuntimeProfile`, `RuntimeRun` and runtime sessions; `RuntimeProfile` is Runtime-owned because it selects execution semantics, despite being administered beside model profiles |
| Tools | `ToolProvider`, `Tool`, `ToolDefinition`, `ToolExecutionHook` and governance lifecycle |
| Secrets | `Vault`, `Secret` and protected value-provider boundaries |
| Work | `Entry`, `WorkItem`, Workplace tasks and interactions; an Entry is the Work-facing published entry point even when it targets a Flow |
| Identity | tenants, workspaces, principals, accounts, memberships, roles, sessions, tokens, preferences and security audit |
| Packs | `InstalledPack`, `PackConfiguration`, `PackProject`, `PackProjectBuild` and pack composition/install lifecycle |
| Sources | `SourceProvider`, `Source`, `SourceVersion`, `SourceConfiguration`, `SourceObservedState`, `SourceImportRecord`, `SourceChannelSnapshot`, `SourceChannelObservedState`, `SourceChannelRefreshRecord`, `SourceRegistryRegistration`, `SourceRegistryObservedState`, `SourceRegistryRefreshRecord` |
| Extensions | `ExtensionRegistration`, `AepEnrollmentSettings`, `AepEnrollmentRequest` and enrollment lifecycle |
| Resource Management | `ManagementOperation` and `BootstrapApplication`; these describe generic lifecycle/application operations rather than a business resource family |
| Shared Resources | no business resource kind; only domain-neutral primitives |

Public kind strings, routes, serialized fields, ETags, generations, scope behavior and persisted table/schema identities remain unchanged. `ResourceKinds` may temporarily forward legacy constants while consumers migrate, but it is a compatibility surface and cannot receive new family kinds.

Family relationships use small provider-neutral ports: Triggers submits a logical Flow target; Flows invokes Agents and Tools; Infrastructure composes adapters; Work records functional task state without owning Flow Runs or Trigger occurrences.

`Agentstration.Flows.*` replaces the historical singular `Agentstration.Flow.*` project and namespace family. This refines ADR-0010 and ADR-0019 without changing their Flow and FlowRun ownership decisions. This decision supersedes ADR-0011's use of a dedicated Management module as a catch-all business owner and preserves ADR-0080's scope behavior under `ResourceManagement`.

API transport ownership is addressed separately by FR-363. `Agentstration.Api` and `Agentstration.Web` remain transport/composition layers during this resource-family extraction and do not acquire business ownership.

## Consequences

The solution remains one modular-monolith process and keeps its deterministic offline defaults. Dependencies point from composition and transport toward family modules, while `Resources` has no dependency on resource management, storage, hosts, or provider frameworks. Family modules do not depend on Web or concrete storage.

Renamed assemblies require solution, architecture-test, coverage and hosted-diagnostics inventories to be updated together. Persistence renames do not imply a database migration because physical schema identifiers are intentionally preserved.
