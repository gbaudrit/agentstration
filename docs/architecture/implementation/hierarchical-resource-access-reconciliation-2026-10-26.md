# Hierarchical resource-access reconciliation — 2026-10-26

This transverse reconciliation compares `main` at `39f2aa4380d019a2a3ea694c62b4e22af432e3f5` with the #789 integration branch at `02ef76cb5e99da8010663c1f81a32fab38a33579`, including the merged implementation and corrective pull requests through #891. It records implementation evidence and documentation drift; it does not introduce a new architecture decision.

## Evidence inspected

- the shared `ResourceScopePolicy`, resource-scope resolver, access-policy evaluator, exact resource identities, SQLite and PostgreSQL stores, and their API and storage tests;
- Flow, Agent, Model Profile, Tool/ToolSet, Data Source, Knowledge Source, Trigger, Runtime, Work, and operational-topology application paths;
- the Console's exact hierarchical context, resource-access administration, Flow Designer, direct execution, and Trigger journeys;
- the shared LikeC4 model and its L1, L2, L3, and API-composition views;
- DAT resource, tenancy, runtime dynamic, deployment, persistence, implementation, and dependency pages;
- accepted ADR-0079, ADR-0119, ADR-0172, and ADR-0176 through ADR-0186; and
- architecture tests plus deterministic API, storage, Console component, and Playwright coverage delivered by the #789 child verticals.

## Implemented scope model

The platform uses six independent coordinates. Conflating any two of them recreates the security and determinism defects addressed by #789.

| Coordinate | Implemented meaning |
| --- | --- |
| Definition owner scope | Exact Instance, Tenant, or Workspace that owns a reusable definition, constrained by the resource-family scope policy. |
| Dependency owner scope | Exact same scope or an ancestor of the definition owner. An ancestor dependency requires an explicit `use` grant; publication pins its identity and integrity evidence. |
| Administrative context | One exact Instance, Tenant, or Workspace selected in Console and authorized for the Principal. It is not inferred from an operational Workspace. |
| Logical authorization scope | Scope used for `discover`, `use`, audit, and resource resolution for the current operation. For Instance and Tenant administration this remains the selected ancestor scope. |
| Operational Workspace | Physical owner of Work, Runs, Trigger occurrences, conversations, sessions, assignments, input, artifacts, acquisitions, projections, and Snapshots. Instance and Tenant contexts use protected operational Workspaces. |
| Principal ownership and visibility | Independent user-data ownership and RBAC/access-overlay decisions controlling who may inspect or mutate the definition or operational record. |

The implemented resource-family matrix is:

| Family | Definition ownership | Durable operational state |
| --- | --- | --- |
| Model infrastructure | Model Provider, discovered Model, and Runtime Profile are Instance-only. Model Profile is Instance, Tenant, or Workspace. | Model observations attach to the exact provider; executions use the governed Workspace placement. |
| Agent | Agent and immutable Agent revision are Instance, Tenant, or Workspace. Agent Deployment remains Workspace-only and is not a durable local MAF instance. | Runtime Run, assignment, attempt, session, event, and active-instance projection remain Workspace-owned. |
| Flow | Editable definition and immutable version are Instance, Tenant, or Workspace. | Flow Run, input request, event, checkpoint, child-run coordination, and artifacts remain Workspace-owned. |
| Tools | Tool Provider, Tool, ToolSet, and immutable ToolSet version are Instance, Tenant, or Workspace. Tool Definition, Tool Category, and Tool execution hook remain Workspace-only. | Invocation and governance evidence remains attached to the executing Workspace and Run. |
| Data Source | Data Source, profile, and immutable profile revision are Instance, Tenant, or Workspace. | Discovery, acquisition target, acquisition, checkpoint, and produced Artifact state remain Workspace-owned. |
| Knowledge | Knowledge Source is Instance, Tenant, or Workspace. | Acquisition, projection, transformation, Flow Run, Artifact, Snapshot, retrieval, index, observed, and session state remain Workspace-owned. |
| Secrets and contracts | Vault, Secret, Parameter, Schema, and Schema Version are Instance, Tenant, or Workspace. | Values remain late-bound; access overlays and audit records are independent from the governed definition. |
| Distribution and extensions | Source resources, Source Provider, Extension registration, and Installed Pack follow the family hierarchy; Source Registry records are Instance-only. | Refresh/import observations retain their exact family scope; Pack Project and build remain Workspace-only. |
| Scheduling | Trigger remains Workspace-owned. | Instance and Tenant administrative Triggers are stored in the protected operational Workspace and retain the independent logical authorization scope and exact Flow target. |

## Dependency and access invariant

For every reusable definition, the accepted edge is:

```text
definition owner scope -> exact same-scope resource
definition owner scope -> exact authorized ancestor resource
```

Descendants, siblings, cross-tenant scopes, ambiguous unqualified matches, stale pins, and name fallback fail closed. `discover` controls whether an inherited resource enters ordinary catalogues and selectors. `use` controls whether it may be referenced or invoked. Same-scope use remains subject to normal RBAC and does not require a descendant grant. Access-policy changes do not mutate resource content, generation, immutable version, integrity hash, origin, or management authority.

Publication and execution preserve exact scope, namespace, UID, generation or immutable version, and the available definition or dependency hash. A later execution Workspace verifies those pins; it never repeats logical name resolution against its own catalogue.

## Reconciled findings

| DAT area | Implementation-backed finding | Reconciliation |
| --- | --- | --- |
| C4 L1–L3 | The modular-monolith containers and stable server responsibilities remain unchanged. Exact administrative context and governed operational topology belong to Identity; exact-scope lifecycle and access evaluation belong to resource families. | Updated the shared LikeC4 descriptions and L3 mapping without introducing a competing structural model. |
| Resource model | Reusable definitions are not uniformly Workspace-owned. Ownership is family-specific; operational state remains Workspace-owned. | Added the enforced family matrix and separated definition, authorization, placement, Principal, and visibility coordinates. |
| Dynamic execution | Root submissions and direct Agent runs receive logical scope and operational Workspace from trusted context. Definition references retain their independent exact owner. | Updated Mermaid submission and Agent execution views and their ADR anchors. |
| Data Source closure | The #751 transitional restriction has been removed in implementation: a profile may pin an authorized ancestor Flow through the shared access overlay. | Removed the obsolete planned restriction from current-state documentation. |
| Knowledge | Knowledge Source definitions are hierarchical; only operational projection and retrieval state is Workspace-isolated. | Replaced the obsolete Workspace-scoped-definition wording. |
| Console context | One hierarchical selector chooses an exact Instance, Tenant, or Workspace. Operational Workspaces are resolved separately and are not business choices. | Replaced the obsolete Workspace-only cookie/selector description. |
| Operational topology | Semantic `TenantKind` and `WorkspaceKind` values identify generated, persisted topology; no authorization rule depends on a reserved GUID. | Removed wording that could imply a deterministic or hard-coded Workspace identity. |
| Deployment and persistence | The direct/Aspire/Compose process topology and module-owned SQLite/PostgreSQL boundaries remain unchanged. The change is logical ownership and placement inside existing modules, not a new service or store. | No structural deployment change was required. |

## Decision-history interpretation and surfaced drift

Accepted ADRs are historical records and were not rewritten during this reconciliation.

- ADR-0185 explicitly supersedes ADR-0180 where ADR-0180 requires direct Instance- or Tenant-owned durable execution state. The remaining ADR-0180 requirements for exact definition identity, authorization, immutable propagation, and non-rebinding still apply.
- ADR-0179 records a transitional restriction that denied ancestor acquisition Flows until #751 exposed Flow identities to the access overlay. The implementation now satisfies that prerequisite through ADR-0176 and ADR-0172. The current DAT describes the delivered behavior, while ADR-0179 remains evidence of the transition.
- ADR-0176 states both that Flow definitions and all exact operations preserve Instance/Tenant/Workspace ownership and that “Flow drafts remain Workspace-owned authoring state.” The implementation and the rest of the decision support exact-scope editable definitions. This internal wording conflict is intentionally recorded here for maintainer review rather than silently editing the accepted ADR.
- No additional architectural choice lacking a decision record was found. ADR-0172 and ADR-0176 through ADR-0186 collectively cover ownership, access overlays, dependency closure, provenance, exact Console context, operational topology, and Trigger placement.

## Validation

The pull request for #892 records the exact LikeC4, Docusaurus, link, and architecture-test commands and results. The documentation changes are complete only when all shared LikeC4 files validate, the full site builds, and focused architecture checks pass.
