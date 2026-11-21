# ADR-0239: Tenant administration discovers explicitly shared ancestor resources

Status: Proposed — 2026-11-20; implementation POC under review in [PR #1256](https://github.com/gbaudrit/agentstration/pull/1256), [FR #1254](https://github.com/gbaudrit/agentstration/issues/1254).

## Context

ADR-0184 distinguishes exact Tenant administration from hierarchical Tenant execution. Resource access policies can explicitly grant an Instance resource to a Tenant, optionally including its descendant Workspaces. The Console currently lists some granted providers through a dedicated Tenant resolution path, while ordinary resource reads exclude Instance candidates before evaluating the policy. Consequently a provider can be listed but cannot be opened, and a granted profile can remain invisible in Tenant mode even though a descendant Workspace can open it.

The requested behavior is that an explicit Tenant grant applies to the Tenant itself. Propagation controls its Workspaces; it must not be required to access the resource from the grant's exact target. This POC proposes the bounded refinement below to ADR-0184's exact-catalog assumption. ADR-0184 itself remains unchanged pending maintainer review.

## Decision

- Keep one exact administrative context, its RBAC, URL-authoritative selection and operational Workspace separation from ADR-0184/ADR-0187.
- Tenant resource discovery can consider the Tenant's own resources and its Instance ancestor. An inherited candidate is returned only after the existing resource access policy grants `discover` to that Tenant for the saved identity. Do not infer access from platform administrator authority.
- Apply this rule to ordinary resource lists and reads by name, exact address or UID, through the common SQLite/PostgreSQL stores. Do not introduce resource-family-specific switch or authorization rules.
- An exact Tenant grant works with or without `includeDescendants`. Only propagation grants access to descendant Workspaces; unrelated Tenants and Workspaces remain excluded.
- Retain ownership checks for writes and operational inventories. Discovering an ancestor does not grant Use or allow modifying the resource or its access policy in Tenant context.
- Removing the grant or changing the policy's bound identity removes inherited visibility through the same existing policy checks.

## Consequences

The Console can retain an explicitly shared provider or profile when switching Instance → Tenant → Workspace. The normal destination load still decides availability; the common frame handles absence. Existing exact administrative ownership and raw owner inventory boundaries remain in place.

The POC updates the previous storage assertion that excluded an explicitly granted Instance resource from Tenant discovery. Tests cover reads, missing grants, optional propagation, Use separation, revocation, inventory exclusion and denied writes. PostgreSQL follows the same code path changes; its live database suite remains optional.
