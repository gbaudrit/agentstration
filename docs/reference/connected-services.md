# Connected Services API

Connected Services model reusable remote-service configuration independently from
Data Sources and Storage resources. Agentstration owns each configured instance;
an AEP extension contributes only immutable type descriptors.

## Type discovery

`GET /api/connected-service-types` returns the types currently published by enabled
Extension Registrations. Every entry identifies the exact registration scope and
extension identity. A type contains a preferred version and immutable versions with:

- a JSON configuration schema and its SHA-256 digest;
- typed `standard` or `secured` inputs;
- typed `standard` or `secured` exports.

The `aep.connected-service-types` capability is version `1.0`. Extensions register
descriptors through `AepExtensionOptions.ConnectedServiceTypes`.

## Managed resources

The following authorized endpoints expose the product resource family:

| Method | Path | Purpose |
|---|---|---|
| `GET` | `/api/connected-services` | List visible resources |
| `GET` | `/api/connected-services/{name}?scopeRef=...` | Read one exact resource |
| `POST` | `/api/connected-services` | Create and validate a resource |
| `PUT` | `/api/connected-services/{name}?scopeRef=...` | Replace desired configuration with `If-Match` |
| `POST` | `/api/connected-services/{name}/reconcile?scopeRef=...` | Refresh observed compatibility |
| `POST` | `/api/connected-services/{name}/deactivate?scopeRef=...` | Disable use with `If-Match` |
| `DELETE` | `/api/connected-services/{name}?scopeRef=...` | Delete with `If-Match` |

A resource pins `extension`, `extensionId`, `typeId`, `version` and `schemaDigest`.
The Extension reference must include its exact `scopeRef` and be owned by the same
scope or an ancestor of the Connected Service. Parameter and Secret bindings are
also exact scoped references. Required inputs, protection, scalar types and JSON
options are checked against the pinned descriptor.

Observed availability is `ready`, `stale`, `unavailable` or `disabled`. A removed
type/version or changed digest becomes `stale`; reconciliation does not rewrite the
desired revision.

Only references are serialized. Parameter and Secret values are resolved at the
internal `IConnectedServiceResolver` boundary for an authorized consumer and Secret
buffers are disposable. Resolved values are never available from the HTTP API.

Storage adapters bind their requirement ids to selected Connected Service export
ids explicitly. No automatic matching and no direct OpenDAL-to-provider-extension
link is part of this API.
