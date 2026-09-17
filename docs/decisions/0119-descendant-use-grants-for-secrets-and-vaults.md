# ADR-0119: Descendant use grants govern Secrets and Vaults

Status: Accepted — 2026-09-17

## Context

ADR-0079 made instance, tenant, and workspace ownership explicit. ADR-0080 allowed Secrets and Vaults at all three levels, but required a Secret and its Vault to have the same owner. Ordinary hierarchical visibility is insufficient authorization to use a credential: an instance-owned Secret must not automatically become available to every tenant.

## Decision

- A reusable `DescendantUsePolicy` contains explicit `use` grants to exact `ResourceScopeRef` targets. A grant may include the target's descendants. An empty policy denies all descendant use; the resource's own scope still uses normal authorization.
- Grant validation resolves the persisted scope hierarchy and accepts only strict descendants of the resource owner. Sibling, ancestor, self, unknown, and cross-tenant targets are rejected. At use time, the consumer must still be in the resource owner's descendant tree.
- Secret and Vault definitions each carry this policy. Management create and update operations validate it before persistence. An omitted policy defaults to an empty policy.
- Runtime Secret resolution uses an exact scope. A reference with no `scopeRef` retains only same-scope compatibility; it does not search ancestors. An ancestor Secret requires an explicit scoped reference and a grant to the consumer.
- A Secret may reference a same-scope Vault without a grant. It may reference an ancestor Vault only when the Vault grants use to the Secret's scope. The Secret-to-Vault grant is checked when metadata is written and again on every value operation. The Vault provider receives the Vault's canonical scope for storage and authenticated encryption identity.
- Secret value resolution remains late-bound through `ISecretResolver`. Neither a reference nor visibility grants use, and no Secret value enters a Management response, export, log, or UI state.

## Consequences

- An instance Secret can be exposed to a selected tenant or workspace without exposing it to sibling tenants. Tenant grants can include their own workspaces when explicitly requested.
- Revoking a grant takes effect on the next resolution or value operation. A Secret whose Vault grant is revoked becomes unavailable until an authorized Vault is selected or the grant is restored.
- The policy is stored in the JSON resource payload; SQLite and PostgreSQL relational schemas do not change. Workspace-owned Secrets and Vaults resolve in their own scope.
- This decision supersedes the same-scope Secret/Vault requirement and Console Vault selector limitation in ADR-0080, and the workspace-only scope limitation in ADR-0040. It refines the visibility-based credential wording in ADR-0090: visibility alone never authorizes use.
