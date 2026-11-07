# Connected Services API

Connected Services model reusable remote-system configuration independently from
Data Sources and Storage resources. Both a configured service and its type are
governed Agentstration resources.

## Connected Service types

`ConnectedServiceType` resources are scoped to an Instance, Tenant or Workspace.
They may be:

- bootstrapped by the product with Core governance;
- created by an operator with user governance;
- materialized from an authenticated AEP contribution and governed by its exact
  Extension Registration.

The builtin `http-endpoint` type is Instance-scoped and Core-owned. Its required
`baseUrl` Parameter input is also exposed as a standard `baseUrl` export.

Every type publishes a preferred version and an additive set of immutable versions.
A version contains a JSON configuration schema and digest, typed `standard` or
`secured` inputs, and typed exports. Mutable metadata, enablement and use policy can
change; an existing version cannot be changed or removed.

The authorized type endpoints are:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/connected-service-types` | List visible type resources |
| `GET` | `/api/connected-service-types/{name}?scopeRef=...&namespace=...` | Read one exact type resource |
| `POST` | `/api/connected-service-types` | Create a type resource |
| `PUT` | `/api/connected-service-types/{name}?scopeRef=...&namespace=...` | Update mutable metadata or add versions with `If-Match` |
| `DELETE` | `/api/connected-service-types/{name}?scopeRef=...&namespace=...` | Delete an unused user-owned type with `If-Match` |

AEP retains its `connected-service-type` contribution contract. Enrollment copies
contributions into extension-namespaced resources and records the Extension
Registration as their management authority. Catalog reads and Connected Service
validation then use the local resources; they do not call the extension live.

Types can also be included as `ConnectedServiceType` documents in Bootstrap
profiles. The profile target supplies the scope when the document omits one.

## Connected Services

The following authorized endpoints manage configured external systems:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/connected-services` | List visible resources |
| `GET` | `/api/connected-services/{name}?scopeRef=...` | Read one exact resource |
| `POST` | `/api/connected-services` | Create and validate a resource |
| `PUT` | `/api/connected-services/{name}?scopeRef=...` | Replace desired configuration with `If-Match` |
| `POST` | `/api/connected-services/{name}/reconcile?scopeRef=...` | Refresh observed compatibility |
| `POST` | `/api/connected-services/{name}/deactivate?scopeRef=...` | Disable use with `If-Match` |
| `DELETE` | `/api/connected-services/{name}?scopeRef=...` | Delete with `If-Match` |

A resource pins an exact type `ResourceReference`, `typeUid`, `version` and
`schemaDigest`. It contains no Extension Registration reference: the type resource
records its own origin and management authority. The type must belong to the same
scope or an ancestor allowed by its use policy. Parameter and Secret bindings are
also exact scoped references. Required inputs, protection, scalar types and JSON
options are checked against the pinned version.

Observed availability is `ready`, `stale`, `unavailable` or `disabled`. A replaced
type identity, missing version or changed digest becomes `stale`; a missing or
disabled type is `unavailable`. Reconciliation never rewrites the desired revision.

Only references are serialized. Parameter and Secret values are resolved at the
internal `IConnectedServiceResolver` boundary for an authorized consumer, and
Secret buffers are disposable. Resolved values are never available from the HTTP
API.

Storage adapters bind their requirement ids to selected Connected Service export
ids explicitly. No automatic matching and no direct OpenDAL-to-provider-extension
link is part of this API.
