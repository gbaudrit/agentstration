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
standard `baseUrl` input is also exposed as a standard `baseUrl` export.

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

## Operation types and discovery

`ConnectedServiceOperationType` is a governed resource with immutable additive
versions. A version defines the JSON schema of one protocol-specific operation,
its interaction shape and an invocation contract. This keeps an OpenAPI operation,
an MCP tool and future protocol operations distinct without hard-coding their
definitions in the product.

An AEP extension contributes operation types with the
`aep.connected-service-operation-types` capability. A Connected Service type
version may then declare an exact discovery Flow and the exact operation-type
revisions that the Flow may emit. Enrollment materializes operation types, Flows
and service types in dependency order. Bootstrap supports
`ConnectedServiceOperationType` documents as well.

The operation-type endpoints are:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/connected-service-operation-types` | List operation types |
| `GET` | `/api/connected-service-operation-types/{name}?scopeRef=...&namespace=...` | Read an exact operation type |
| `POST` | `/api/connected-service-operation-types` | Create an operation type |
| `PUT` | `/api/connected-service-operation-types/{name}?scopeRef=...&namespace=...` | Add versions or update mutable properties |
| `DELETE` | `/api/connected-service-operation-types/{name}?scopeRef=...&namespace=...` | Delete an unused operation type |

Discovery runs as the exact Flow pinned by the service type and uses contract
`connected-service-operation-discovery/v1`. Its input exposes only exact resource
identity and caller context, never resolved credential values. The bounded output
is validated against the declared operation types before materialization.

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/api/connected-services/{name}/operation-discoveries?scopeRef=...` | Start a durable discovery |
| `GET` | `/api/connected-services/{name}/operation-discoveries?scopeRef=...` | List discoveries for a service |
| `GET` | `/api/connected-service-operation-discoveries/{id}?namespace=...` | Observe and synchronize a discovery |
| `POST` | `/api/connected-service-operation-discoveries/{id}/cancel?namespace=...` | Cancel an active discovery |
| `GET` | `/api/connected-services/{name}/operations?scopeRef=...` | List materialized operations |
| `GET` | `/api/connected-service-operations/{name}?scopeRef=...&namespace=...` | Read an exact materialized operation |

Materialized operations are read-only. Rediscovery preserves their deterministic
identity, updates validated definitions and marks operations omitted from the
latest successful manifest as unavailable instead of deleting them.

## Connected Services

The following authorized endpoints manage configured external systems:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/connected-services` | List visible resources |
| `GET` | `/api/connected-services/{name}?scopeRef=...` | Read one exact resource |
| `GET` | `/api/connected-services/{name}/usages?scopeRef=...` | List dependent resources reported by consumer verticals |
| `POST` | `/api/connected-services` | Create and validate a resource |
| `PUT` | `/api/connected-services/{name}?scopeRef=...` | Replace desired configuration with `If-Match` |
| `POST` | `/api/connected-services/{name}/reconcile?scopeRef=...` | Refresh observed compatibility |
| `POST` | `/api/connected-services/{name}/deactivate?scopeRef=...` | Disable use with `If-Match` |
| `DELETE` | `/api/connected-services/{name}?scopeRef=...` | Delete with `If-Match` |

A resource pins an exact type `ResourceReference`, `typeUid`, `version` and
`schemaDigest`. It contains no Extension Registration reference: the type resource
records its own origin and management authority. The type must belong to the same
scope or an ancestor allowed by its use policy. Parameter and Secret bindings are
exact scoped references; literals are typed JSON scalars stored directly in the
resource. Required inputs, protection, scalar types and JSON options are checked
against the pinned version. Secured inputs accept only Secret references.

Observed availability is `ready`, `stale`, `unavailable` or `disabled`. A replaced
type identity, missing version or changed digest becomes `stale`; a missing or
disabled type is `unavailable`. Reconciliation never rewrites the desired revision.

References and literal configuration are serialized. Literals are limited to 64
KiB and visible through authorized resource administration. Parameter and Secret
values are resolved at the internal `IConnectedServiceResolver` boundary for an
authorized consumer, and Secret buffers are disposable. Resolved Parameter and
Secret values are never available from the HTTP API.

Storage adapters bind their requirement ids to selected Connected Service export
ids explicitly. No automatic matching and no direct OpenDAL-to-provider-extension
link is part of this API.

## Console

The administration Console exposes the same family-owned API through these routes:

- `/connected-services` lists the permission-aware scoped catalog and filters by type or observed availability;
- `/connected-services/new` selects an exact `ConnectedServiceType` resource revision, renders its JSON schema and binds every declared input manually to a typed literal, exact Parameter or Secret as allowed by the input contract;
- `/connected-services/{name}?scopeRef=...` provides Overview, Definition, Access and YAML tabs. Definition updates options and bindings with the resource ETag; YAML exposes the read-only resource manifest.

Reconciliation and deactivation always require explicit confirmation. Reconciliation
refreshes compatibility evidence but never silently changes the pinned type UID,
version or schema digest. The Console never displays Secret values and does not
perform provider-specific Storage configuration.
