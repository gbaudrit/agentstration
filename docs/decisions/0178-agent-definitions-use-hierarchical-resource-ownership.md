# ADR-0178: Agent definitions use hierarchical resource ownership

## Status

Accepted

## Context

Agent definitions and their immutable revisions were originally Workspace-owned. Hierarchical Flow ownership requires one reusable Agent graph to retain the same identity and dependencies when it is invoked from Instance, Tenant, or Workspace scope. Resolving an Agent or one of its dependencies again from the execution Workspace can select a shadowing resource and change the behavior of an already published Flow.

Agent execution instances are transient projections assigned to Runtime Workers. They are not durable management deployments. Invocation state has its own ownership model and is deliberately separate from definition ownership.

## Decision

- `Agent` and `AgentRevision` may be owned by Instance, Tenant, or Workspace scope. Their durable identities include the exact `ResourceScopeRef`; equal namespace and name values may coexist at different scopes.
- An Agent dependency must be owned by the Agent scope or by one of its ancestors. Ancestor use requires an explicit resource-access grant. Descendant, sibling, cross-tenant, ambiguous, and unauthorized references fail closed.
- Publishing an Agent revision records the exact scope, UID, namespace, kind, generation or immutable version, and integrity evidence for its Model Profile, Runtime Profile, direct Tools, and published ToolSets. Runtime resolution verifies those pins and reports stale dependencies instead of rebinding by name.
- Publishing a Flow resolves each static Agent reference relative to the Flow owner scope, verifies descendant-use authorization, ensures the immutable Agent revision exists, and writes the exact Agent scope and generation into the published Flow version.
- Runtime references, Flow runtime bindings, and execution material carry the exact Agent owner scope, Agent UID, revision UID, generation, and definition hash. The invocation Workspace or other execution context never participates in selecting the Agent graph.
- Bootstrap and HTTP operations accept exact Instance, Tenant, and Workspace Agent scopes. Compatibility operations without a scope may remain, but ambiguity is an error.
- Durable execution state remains outside this decision. Its exact invocation-scope model is owned by issue #811 and must not change or rebind the pinned Agent graph.

## Consequences

- Existing pre-release databases may be reseeded; no compatibility migration for released data is required.
- Updating or replacing a pinned dependency makes an older Agent revision fail closed when the exact identity no longer matches.
- Console links include `scopeRef` so same-named Agents remain addressable, while access-policy administration stays in the shared hierarchical-resource UX.
- The legacy `AgentDeployment` management resource remains Workspace-only for compatibility and is not used to persist local Microsoft Agent Framework instances.
- This decision supersedes the Agent and AgentRevision Workspace-only portions of ADR-0080. ADR-0172 remains authoritative for descendant access, ADR-0177 for Model and Runtime ownership, and ADR-0168 for external Runtime assignment.
