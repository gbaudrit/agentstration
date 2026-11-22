# ADR-0241: Bootstrap declares local descendant resource access

Status: Accepted — 2026-11-18

## Context

ADR-0172 separates descendant Discover/Use authorization from resource ownership
and manifests. A Model Provider remains instance-owned, while a Model Profile and
an Agent may belong to a Tenant or Workspace. The Ollama Bootstrap example needs
explicit authorization between these scopes before validating each consumer.
Previously Bootstrap could create definitions but could not prepare these grants.

## Decision

Add a `ResourceAccessPolicy` administrative Bootstrap declaration. Its selector
contains a resource kind, name and optional namespace in the current profile's
exact owner scope. Grants use explicit active `targetLevel` name/ID selectors,
Discover/Use actions and optional descendant propagation, disabled by default.

This declaration is local deployment input, not serialization of the overlay row
and not a Pack or extension resource. It does not change ADR-0172's policy storage,
authorization evaluator, resource manifests, ownership or provenance. Bootstrap
resolves the current UID and generation/version at application time and delegates
the write to the existing authorized, audited `IResourceAccessPolicyService`.
There is no name-based fallback in access evaluation. Both preview and apply reject
managed governance contexts, so an extension bundle cannot use the shared Bootstrap
handler registry to administer local grants.

Preview checks that the resource exists or is planned earlier in the same
application, resolves targets and rejects self, sibling, ancestor, duplicate and
cross-Tenant grants. It performs no policy writes. Apply creates only a missing
overlay. Existing policies, including revoked grants, are preserved; Bootstrap
never merges, reconciles or broadens them. Ordinary administration remains the
update boundary. A deleted/recreated resource has another UID and needs an explicit
new application or normal administration to authorize it.

The Ollama example orders instance provider and its Tenant grant, Tenant model
and its Workspace grant, then the Workspace Agent. It reuses the already shared
`maf-builtin` runtime. External orchestration still owns extension processes,
shared-key files and model acquisition.

## Consequences

- A fresh three-scope Bootstrap can create a usable model/Agent dependency chain
  without weakening default-deny descendant access or requiring manual grants.
- Resource names are authoring selectors only; persisted grants bind exact current
  identities through existing family-neutral identity lookup.
- The handler supports only the local administrative declaration. Pack import and
  export, extension contributions and resource integrity hashes remain unchanged.
- Editing a resource generation/version or revoking a grant can make a dependent
  consumer unavailable. Bootstrap preserves that administrative decision; operators
  update or recreate policies through their normal boundary as needed.
