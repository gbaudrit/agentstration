# ADR-0239: Discovered operations can inherit Connected Service access

## Status

Accepted

## Context

Issue #1269 needs one explicit choice to expose all discovered operations under
an owning Connected Service's access rules. Discovery materializes operations,
but copying grants would retain obsolete rights after policy changes. Visibility
is enforced inside both resource stores, while use is evaluated by the shared
resource access service and the Connected Service consumer boundary.

## Decision

Add `ConnectedServiceProperties.ExposeOperationsToDescendants`, defaulting to
`false`. When enabled, a discovered operation selects its exact owning service as
its access-policy source for both `discover` and `use`. SQLite and PostgreSQL
project that source consistently for discovery and resource identity evaluation.
The projection requires the service UID, address, namespace and owner scope to
match the operation's provenance reference and physical ownership. No grants are
copied or created. Without this choice, existing independent operation policy
behavior remains in place; individual operation policy editing is deferred.

The internal resource identity snapshot may carry the current exact policy-source
identity. The resource access service evaluates the same requested action against
that source after checking the consumer's ancestry. Source selection does not
change the resource's identity, owner or revision. Inherited sources must share
the owner scope. The only producer is the validated operation-to-service link.

Changing only the exposure choice changes the service ETag while retaining its
connection generation, so existing operation pins remain usable. Other desired
configuration changes retain the existing generation and reconciliation rules.
The exposure flag controls neither discovery execution nor operation activation.
Service use grants, service/type availability, exact revisions, operation-type
access and consumer authorization continue to apply during consumption.

## Consequences

- Existing definitions retain their previous access behavior without migration.
- Current and future discovered operations inherit live policy changes, including
  revocation, without rediscovery or grant synchronization.
- Discover-only grants never imply use; operation enablement and availability
  remain separate checks.
- The SQL providers depend on the family contracts for exact-link validation;
  generic authorization does not import a Connected Service business module.
- No new per-operation access editor, runtime dispatch or protocol adapter is added.
