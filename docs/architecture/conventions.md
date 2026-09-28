# Architecture documentation conventions

The `docs/architecture/` tree is the canonical Agentstration Technical Architecture Document (DAT). It is maintained progressively: existing pages remain available until their still-valid information has a canonical destination and all inbound links have been reconciled.

## Sources of architectural truth

Architecture documentation connects several complementary artifacts. None of them replaces the others.

| Artifact | Owns | Does not own |
| --- | --- | --- |
| LikeC4 | The shared structural model, element identities, relationships, and C4 views | Runtime sequences, state machines, decision history, or exhaustive implementation inventories |
| DAT Markdown | Architectural narrative, scope, constraints, quality attributes, implementation mapping, and links between evidence | A second structural model that competes with LikeC4 |
| Mermaid | Dynamic behavior such as sequences, states, lifecycles, and focused data flows | Canonical C4 structure |
| ADRs | Significant decisions, their context, status, and consequences | A current architecture inventory or an implicit replacement for implemented evidence |

Git is the source of truth for all four artifact types. Published documentation is built from the repository and must not depend on a reader running LikeC4 locally.

Architecture statements must be evidence-based. Current code, executable composition, storage configuration, architecture tests, and accepted ADRs are inspected together. When implementation and an accepted ADR disagree, documentation records the discrepancy for reconciliation; it does not silently redefine the decision or normalize the implementation.

## One shared LikeC4 model

Agentstration has one logical LikeC4 model. It may be split across several source files for maintainability, but every structural view projects the same elements and relationships. Plane-, module-, or feature-specific files may extend that model; they must not define independent copies of the Agentstration system, its containers, or shared external systems.

The model sources belong under `docs/architecture/likec4/`. Generated output is build material and is not an independently edited architecture source.

Stable model identifiers describe architectural responsibilities rather than current project names. A project, namespace, or class can be linked as implementation evidence without becoming the identity of a C4 element.

## C4 modeling levels

### L1 — System Context

L1 represents Agentstration as one software system. It includes evidence-based people or roles, external software systems, and their principal relationships with Agentstration.

L1 excludes internal processes, modules, databases, projects, and framework details. Optional integrations must be identified as optional; planned integrations must not be presented as current mandatory dependencies.

### L2 — Containers

A C4 Container is an independently running or deployable application, process, client shell, data store, or other significant runtime unit. It is not synonymous with a Docker container.

An element belongs at L2 when repository evidence shows at least one of the following:

- an executable or independently hostable process boundary;
- an independently deployed client or extension boundary;
- a significant persistent store with distinct ownership or lifecycle;
- a protocol boundary whose deployment has material architectural consequences.

Logical responsibilities such as Management, Work, Flow, Runtime, Identity, Tooling, Sources, Packs, or Triggers are not promoted to L2 merely because they have projects or storage adapters. When they execute within the authoritative server, they are modeled as L3 components of that container.

Development orchestrators, replicas, network zones, optional topology variants, and mappings from logical stores to physical services belong primarily in deployment views. A deployment node may instantiate an L2 container; it does not create a second logical container identity.

### L3 — Components

L3 opens a significant L2 container and shows stable architectural responsibilities and their important relationships. Components are selected because they own meaningful behavior, policy, or state, not because a `.csproj`, namespace, service, endpoint class, or database table exists.

The server view may include responsibility-oriented components for resource-family management, Work, Flow, Runtime, Identity and authorization, Tool execution and governance, transport composition, persistence coordination, and background execution when supported by current evidence.

Implementation mappings may list the projects that currently realize a component. Such mappings are informative and may change without changing the C4 identity.

### L4 and detailed code structure

The DAT does not maintain exhaustive C4 L4 diagrams. Focused implementation diagrams are allowed only when they explain a concrete design that prose or a smaller diagram cannot express; they are not part of the canonical L1-to-L3 navigation.

## Structural, dynamic, and deployment views

Structural C4 views come from LikeC4. Every published L1, L2, and selected L3 view has a stable view identifier and participates in the natural L1 → L2 → L3 navigation where a deeper view exists.

Dynamic views use Mermaid for architectural sequences, states, and lifecycles. They reuse the terminology of the shared structural model and link back to the relevant C4 elements, but they do not redeclare structural ownership. A dynamic view stays above method-level tracing and documents an implemented scenario or an explicitly marked planned scenario.

Deployment views distinguish logical containers from environment-specific placement. They may describe direct standalone startup, Aspire development orchestration, Compose variants, SQLite and PostgreSQL profiles, and optional provider topologies without changing the logical C4 model.

## Target DAT information architecture

The target tree is introduced progressively rather than created as empty scaffolding:

```text
docs/architecture/
  overview.md                 DAT entry point and navigation
  conventions.md              ownership and modeling rules
  migration-inventory.md      source-to-destination migration ledger
  likec4/                     one logical LikeC4 model, split when useful
  context/                    scope, stakeholders, constraints, quality goals
  c4/                         narrative pages embedding or linking L1/L2/L3 views
  runtime/                    dynamic execution scenarios and lifecycles
  data/                       resource, persistence, ownership, and consistency models
  deployment/                 standalone and environment-specific deployment views
  cross-cutting/              identity, isolation, security, observability, resilience
  implementation/            project mapping, dependency rules, and current inventory
```

ADRs remain under `docs/decisions/` and are linked from the relevant DAT pages. Concept and reference documentation remains in its existing sections unless its content is specifically architectural and is migrated with an explicit destination.

## Progressive migration rules

- Record every legacy source and its destination in the migration inventory.
- Move or rewrite content only after checking it against current implementation and accepted ADRs.
- Preserve unique still-valid information before removing or reducing a page.
- Keep compatibility links or explicit replacement pointers when a published path changes.
- Replace structural Mermaid diagrams only after the corresponding LikeC4 view is published and linked.
- Reconcile dynamic Mermaid diagrams instead of copying them into parallel pages.
- Mark planned, preview, optional, and not-yet-implemented behavior explicitly.
- Validate Docusaurus links and rendering after every increment.

An architecture-documentation reconciliation may correct documentation drift and report missing decisions. It must never invent an ADR or architectural decision to make the documentation appear consistent.
