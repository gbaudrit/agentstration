# ADR-0080: Management resource kinds have an initial scope policy

Status: Accepted — 2026-09-07

## Context

ADR-0079 introduced generic instance, tenant, and workspace ownership, but deliberately left the allowed scope of each resource kind unspecified. Generic storage support alone must not let a caller place a resource at a level where its dependencies or execution data cannot be resolved safely.

Model configuration is shared within a tenant in the first executable multi-scope increment. Agents, Flows, Entries, Triggers, deployments, and execution state remain workspace-owned. Secrets are needed by resources at all three levels, but allowing a Secret to use a broader Vault would make deletion, encryption identity, and authorization ambiguous.

## Decision

The initial resource-kind policy is:

| Resource kind | Allowed ownership |
| --- | --- |
| `ModelProvider`, `ModelProfile`, `RuntimeProfile` | tenant |
| `Agent`, `AgentRevision`, `AgentDeployment`, `Trigger` | workspace |
| `Vault`, `Secret` | instance, tenant, workspace |
| `ExtensionRegistration` from configuration or Aspire | instance |
| manually created `ExtensionRegistration` | tenant |
| `ToolProvider`, `Tool`, `ToolExecutionHook` | workspace until their dependency model is reviewed |
| installed Pack | the Pack target scope |
| Pack Project and Composer build | workspace |

A Secret and its referenced Vault must have the exact same `scopeRef`. This invariant is validated on metadata creation and update and is also rechecked before every value operation. Vault-provider storage identity and authenticated encryption bind to the canonical scope reference.

References may point only to the consumer's own scope or an ancestor. An omitted `scopeRef` resolves only when exactly one matching visible resource exists; homonymous visible resources require an explicit scope. Siblings and descendants are rejected.

Workspace Bootstrap Profiles and Packs remain the default authoring experience. A workspace-targeted application may contain tenant-only model resources followed by workspace-owned consumers; those fixed-scope resources are deterministically assigned to the current tenant. This is a resource-kind rule, not an operator-selected promotion. Tenant and instance Pack targets remain explicit, and the installed Pack record is owned by that exact target.

The Console exposes scope badges on Management resource inventories. Vault and Secret creation exposes only authorized instance, tenant, and workspace targets, carries exact scope through detail URLs, and filters the Vault selector to the Secret's selected scope.

## Consequences

- A tenant's workspaces share one model-provider/profile/runtime-profile catalog without duplicating definitions.
- Agents remain aligned with workspace-only Flow and Entry storage.
- Secret values cannot cross Vault ownership or encryption boundaries.
- Configuration-backed extension discovery runs once at instance scope; manual registrations remain tenant-administered.
- Tool resources stay workspace-only until a separate dependency analysis expands them.
- Existing generated databases must still be reseeded as required by ADR-0079.
- This decision refines ADR-0079 and supersedes the workspace-only ownership statements for Secrets, Vaults, extension registrations, and model resources in ADR-0040, ADR-0053, ADR-0057, and ADR-0063.
