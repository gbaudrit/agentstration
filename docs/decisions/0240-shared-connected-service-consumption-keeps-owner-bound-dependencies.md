# ADR-0240: Shared Connected Service consumption keeps owner-bound dependencies

## Status

Accepted

Supersedes the default-off and consumer-side operation-type authorization portions
of [ADR-0239](0239-discovered-operations-can-inherit-connected-service-access.md),
and the exact-owner read restriction for Tenant administration in
[ADR-0184](0184-console-context-is-a-single-exact-administrative-scope.md).
The other decisions in those records remain applicable.

## Context

The follow-up to #1269 requests default-on operation exposure and reports that a
Workspace can select a shared operation but cannot save its Data Source. The
editor requires an operation-type catalogue entry, although that administrative
type is private to the service owner. Backend resolution also requires consumer
access to owner-bound types and the native acquisition Flow. Separately, Tenant
administration filters Instance resources out before evaluating explicit grants.
These restrictions prevent consumption under the service's granted access rules.

## Decision

- Omitted `exposeOperationsToDescendants` values and new Console forms default to
  `true`. Explicit persisted `false` values remain opt-outs. The form uses the
  existing switch component. Exposure does not activate an operation.
- The generic resource access policy is the authority for service consumption.
  The legacy embedded service `usePolicy` remains serializable but no longer acts
  as a second consumer gate. The Console edits grants in the shared Access tab.
- Tenant administration reads include Instance resources only after the existing
  `discover` evaluation authorizes the selected Tenant. Exact-owner inventories,
  commands, context selection and business Workspace isolation remain unchanged.
  Both generic SQL resource stores apply this rule.
- A consumer obtains a pinned operation contract through the operation service,
  after ancestry, exact identity/revision, availability and current operation/service
  `use` checks. Consumer scope comes from the authenticated request. Only that
  operation's exact type version is returned, without granting catalogue access.
- Configured service and operation dependencies are read privately and validated
  against the owning resource's scope and exact pins. The owner must still be
  authorized to use its type, bound inputs and adapter Flow. Types, secrets and
  Flows receive no consumer grants, and secret values remain internal.
- The native acquisition adapter resolves its exact Flow with the operation
  owner's permissions. A server-only, JSON-ignored definition authorization scope
  is accepted only for internal accepted compositions. The Flow Run keeps the
  consumer's execution Workspace and logical scope. Authored composition paths
  retain their existing Flow authorization behavior.
- Current access and exact revisions are revalidated on save and acquisition.
  Revocation, disabled or unavailable operations, stale pins and unrelated scopes
  continue to fail closed. Exposure-only edits retain the service connection
  generation and still require the current ETag.

## Consequences

- A granted service is consumable without independently exposing its implementation
  catalogue. Operation contracts are projections through an authorized operation,
  not standalone grants on dependencies.
- Definitions that omitted the exposure flag now inherit the service's live grants.
  Deployments relying on the legacy embedded service policy must configure the
  generic service Access policy; legacy grants are not copied automatically.
- Tenant catalogues can show explicitly shared ancestors. This changes reads, not
  ownership, write authorization or exact inventory semantics.
- Offline API coverage must exercise a shared Instance service with private type
  and Flow catalogues through contract inspection, Data Source save and acquisition,
  together with access revocation and cross-scope denials.
