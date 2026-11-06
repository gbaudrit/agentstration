# ASP content profile 1.0

## Scope and version dimensions

ASP (Agentstration Storage Protocol) is an application contract over MCP, not a new
transport. The delivered version is exactly `1.0`, profile `asp.content/1`. MCP
initialization negotiates the transport protocol independently. Provider versions
(e.g. `sample.memory@1.0.0`) identify backend behavior independently of both.
Clients reject unknown ASP versions/profiles, absent mappings, incompatible schemas
and unimplemented advertised capabilities. They may pin an exact provider version.
A new ASP contract needs a new compatible SDK on both ends; no inferred compatibility.
NuGet package version `0.1.0` is a fourth, distribution-only dimension.

AEP discovers/configures extensions and maps service operations to an MCP endpoint
and Tool name (ADR-0171). ASP defines storage semantics on that endpoint. This SDK
does not add an AEP storage contribution; #1040 owns discovery/enrollment/product
bindings. A storage operation is not automatically an Agent Tool. The existing
Artifacts lifecycle (ADR-0151) remains unchanged.

## Authority and effective logical store

`AspStoreRef = { storeId, bindingId, revision }` is an exact logical identity:
storeId identifies the dataset; bindingId identifies the configured provider
binding; revision pins that binding's configuration. It contains no physical path,
backend URI or credential. Changing placement/configuration requires a new revision;
a historical reference must never resolve through a newer binding implicitly.

The extension configures `AspStoreBinding` entries mapping exact logical targets
to provider instances and maximum access policies. One generic MCP Tool group serves
all these targets. Every invocation carries `{ store, arguments }`; `store` is the
caller-selected `AspStoreRef`, while `arguments` carries the operation inputs below.
A target selection is **not authority**. The extension resolves only configured
identities and its authorization callback derives the authenticated subject,
current permission revision and permitted grant on **every** invocation. Unknown
identities/revisions and unauthorized targets fail. No callback means no binding.

The SDK requires the authorized grant to match the selected target and intersects
it with that target's configured maximum policy. The provider receives this trusted
resulting context and enforces it independently. Sharing an extension, provider
instance, filesystem directory or database table never grants cross-store access.
An authorized caller, including an Agent granted direct access, may select any
target it is permitted to use through these same generic Tools.

Policies constrain list/stat/read/write operations, an ordinal logical prefix,
optional inclusive `keyFrom` / exclusive `keyBefore` bounds, and per-call page/read/
write limits. Compatible prefixes are narrowed, key intervals intersected, booleans
ANDed and payload limits minimized. Unsupported restriction fields fail rather
than being ignored. MCP annotations and caller-supplied identifiers confer no rights.

## Extensible logical-to-physical resolution

StoreId, BindingId and Revision are opaque bounded identifiers, with no backend
syntax or closed list of storage types. The extension owns their resolution to
physical placement. Different targets may use the same physical provider or
separate provider instances/backends without changing the ASP Tool schemas.

Physical configuration is provider-specific opaque JSON governed by the provider's
JSON Schema and versioning. For example, an S3 provider could define bucket/prefix;
an Azure provider account/container/prefix; a file provider an authorized root;
a SQL provider a connection reference/schema/table. These are examples of possible
provider configuration, not fields or backend implementations built into ASP.
Keys/values and secret-access references must retain their provider-defined meaning.
The exact binding revision pins the configuration; remapping an existing identity
to another physical destination without changing revision violates the contract.

AEP configuration discovery/versioned option sets and the extension's provisioning
path own creation and configuration of these mappings; #1040 integrates that product
lifecycle. ASP `describe` returns the selected target's provider configuration schema
and effective capabilities. Content calls select an existing target and do not
create or reconfigure physical destinations. This SDK ships no backend enum,
provider configuration-value transport, target-provisioning API or connection secret
management. Its sample accepts the empty provider configuration object only.

## Agentstration parameter relays are a product capability

Agentstration may expose a Tool that relays an existing Tool call while fixing
parameters such as `store`. The Agent sees only the remaining permitted parameters;
the product must inject fixed values, reject overrides and enforce its authorization
and constraints before calling the original generic Tool. This mechanism applies
to arbitrary Tools, including ASP Tools. It is owned by Agentstration, **not** the
ASP protocol or SDK. #1044 owns the generic relay capability; #1040 owns
Storage integration and reuses that relay where fixed-parameter exposure is needed.
Direct use of an authorized generic ASP Tool remains valid.

Authorization is mandatory on both direct and relayed ASP calls. The SDK invokes
the extension's authorization callback before provider execution; the callback owns
the permission source and must verify the authenticated caller's target/operation
rights. With a relay, the extension normally authenticates the Agentstration service
identity, while Agentstration authorizes the originating caller locally. Enforcing
end-user permissions at the extension also requires verifiable delegation through
the authentication layer; a `userId` business argument is never trusted authority.
This SDK does not introduce an end-user delegation protocol or permission database.

The SDK contains no relay component, preconfigured Agent Tool publisher or product
Tool configuration model. Its typed client keeps a selected target for consumer
convenience and includes that target explicitly in each generic wire invocation;
this is not an MCP Tool relay. Future query profiles must preserve the same target
and authorization boundaries and require actual provider/consumer support.

## MCP operation mappings and JSON Schemas

A configured service Tool prefix `P` maps to these known operation suffixes. The
sample exposes `asp_describe`, `asp_list`, `asp_stat`, `asp_read` and `asp_write`
once for all targets, with no per-target Tool names or schemas. A generic operation
is published when at least one configured target supports it. The selected target's
`describe` result determines its capabilities: `asp_write` can exist on the service
while a read-only target explicitly rejects writes. Every call is reauthorized.

All Tool inputs have this envelope:

```json
{
  "store": { "storeId": "documents", "bindingId": "binding-42", "revision": "7" },
  "arguments": { "requestId": "list-1", "prefix": "notes/", "limit": 10 }
}
```

| Operation | `arguments` | Structured success data |
| --- | --- | --- |
| `P_describe` | `{ requestId }` | ASP/profile/provider versions, effective StoreRef, effective policy, capabilities, provider configuration schema |
| `P_list` | `{ requestId, prefix, limit, cursor? }` | `{ objects: AspObject[], cursor? }` |
| `P_stat` | `{ requestId, reference }` | `AspObject` |
| `P_read` | `{ requestId, reference, offset, maxBytes }` | `{ item: AspObject, offset, contentBase64, endOfObject }` |
| `P_write` | `{ requestId, notAfter, key, contentBase64, expectedVersion? }` | `{ item: AspObject }` |

Store and argument objects are closed: unknown fields, physical locations,
permission overrides, missing required properties, null required values and wrong
types fail. Store/binding/revision identifiers are bounded to 128 characters.

The canonical input/output JSON Schemas are in [schemas/](schemas/), generated from
`AspSchemas.Input/Output` and published as MCP `inputSchema` / `outputSchema`.
All objects are closed (`additionalProperties: false`). The typed client checks
these schemas structurally before use. Integer fields are not strings. Semantic
constraints below apply in addition to the JSON type schemas. The schema snapshots
are checked against the executable schema by the conformance suite.

`AspObject = { key, version, length, sha256, reference }`. Length is a non-negative
byte count; digest is lowercase SHA-256 hex. Key is a relative logical name of at
most 256 characters; no absolute paths, backslashes, control characters, empty or
`.` / `..` path components. Prefix is an empty or bounded logical prefix, optionally
ending in `/`. Prefix matching and key ordering are ordinal. Never translate these
names directly into physical filesystem paths without a safe provider mapping.
Versions are opaque provider identities, not sortable timestamps.

Each response is `{ aspVersion, requestId, data? , error? }`, with exactly one of
data or error. Errors have `{ code, message }` and MCP `isError: true`; success has
`isError: false`. The SDK also emits the same serialized JSON as MCP text content
for transport compatibility; clients require the structured form and never parse
prose as an authoritative result. RequestId is a nonblank technical identity of
1–128 characters, echoed exactly. Errors before an identity can be validated use
`unknown`. Object bytes are canonical base64: no JSON key rewriting, charset
conversion, flattening or provider-specific metadata interpretation.

The reader interface supplies list/stat/read and a provider-owned configuration
schema. The optional writer interface supplies atomic writes. The example's
configuration schema accepts `{}` only. Configuration values and physical placement
are trusted provisioning inputs, absent from business calls. Custom JSON property
names/values remain opaque; ASP does not rewrite them.

## References, versions and pagination

Object references and pagination cursors are opaque signed tokens. The SDK binds
them to exact store/binding revision, authenticated subject, authorization revision,
effective policy, token kind and expiry. Cursors additionally bind prefix and page
size. A changed requestId is allowed for the next page. Forgery, object-as-cursor,
cross-store use, changed page query, altered permissions and expired tokens fail.
The sample signing key is process-local: server restart invalidates outstanding
tokens. Tokens expire after 15 minutes; reacquire a list/reference after expiry.
They are not secrets or delegated authorization; permission is rechecked per call.

List returns at most `limit` objects, strictly ordered, strictly after the last key
in the cursor. A provider continuation must equal the last returned key. Empty
pages cannot continue. Pagination is a live lexical walk, **not** a point-in-time
snapshot: concurrent insertions before the cursor may be missed. References pin an
exact version; overwritten versions fail `version_unavailable`, never return newer
content. Read is a bounded range, supports EOF, and returns exact requested bytes
up to EOF. Whole-object reads and write receipts are integrity checked.

## Write identity, conflicts and bounded retries

A write includes a stable requestId and absolute UTC `notAfter`, strictly in the
future and no more than five minutes ahead at the commit boundary. Keep the full
original request, identity, deadline, store and authorized context for retries.
Reuse an identity only for the identical request. Missing `expectedVersion` means
create-only; replacement requires the exact current version. Concurrent creates or
stale versions fail `conflict`. Reusing a live identity with different content,
key, expectation, deadline or policy fails `idempotency_conflict`.

The provider must atomically commit content and its receipt. Within the validity
window an exact retry returns the original metadata/version without writing again,
even if the object has subsequently changed. A receipt acknowledges that original
commit, not current readability. Expired requests always fail `request_expired`,
including after a receipt has been purged. A new deadline is a new operation and
must use a new identity. There is no durable exactly-once promise across provider
restarts unless a provider documents and implements a durable receipt ledger.

The sample caps the entire shared physical namespace at 128 objects / 1 MiB and
the receipt ledger at 128 entries. It refuses capacity without evicting live
receipts. Expired receipts can be collected on subsequent writes. Cancellation is
checked before the atomic commit; there is no cancellable I/O inside it. Backend
providers must implement an equivalent atomic boundary or decline writer support.
No generic SDK retry loop can manufacture this guarantee for a non-atomic backend.

## Limits, cancellation, timeouts and errors

Profile ceilings: 100 objects/page, 65,536 decoded bytes/read or write, 100,000
characters/call arguments, 200,000 characters/structured result, 4,096
characters/reference, JSON depth 32, at most 32 configured target mappings. Policies may
reduce these limits. The sample uses 10/page and 4,096 bytes per call and limits
HTTP request bodies to 100,000 bytes. Hosts must bound transport bodies before MCP
JSON parsing; SDK argument checks occur after transport deserialization. Providers
must bound total storage, concurrency and their own I/O.

Client operation timeout defaults to 30 seconds and can be reduced. Server linked
timeout is 30 seconds. Both propagate cancellation to authorization and providers.
Providers must honor cancellation for long-running work; the SDK cannot terminate
an uncooperative external implementation. Cancellation, timeout, broken transport
or invalid write receipt can leave a commit outcome unknown: never assume rollback
or report success. Retry the exact original write within its original window, or
reconcile after expiry. The SDK's `cancelled` error makes uncertainty explicit;
transport cancellation may instead surface as an `OperationCanceledException`.
A provider failure never creates a success receipt in the SDK.

| Code | Meaning |
| --- | --- |
| `invalid_request` | Wrong schema, key, base64 or input range |
| `forbidden` | Missing/revoked authorization, unauthorized/unknown target, widened scope |
| `invalid_reference` | Forged, expired, wrong-context/versioned binding or wrong-kind token |
| `unsupported_operation` | Missing/unknown mapping or unsupported reader/writer operation |
| `incompatible_version` / `incompatible_schema` / `incompatible_provider` | Consumer compatibility check failed |
| `unsupported_capability` | Consumer cannot honor advertised guarantees |
| `limit_exceeded` / `capacity_exceeded` | Per-call limit or bounded storage/receipt capacity refused |
| `not_found` / `version_unavailable` | Missing object or exact content version no longer available |
| `conflict` / `idempotency_conflict` / `request_expired` | Conditional write or retry identity/deadline failed |
| `invalid_result` | Malformed, inconsistent, out-of-scope or integrity-invalid provider/peer response |
| `cancelled` | Cancelled/expired execution; mutation outcome may be unknown |

Unexpected infrastructure exceptions remain MCP failures. Never serialize provider
stack traces, credentials, full documents or physical backend configuration as
business errors. Contract validation errors contain technical diagnostic messages,
not product UI copy. Product consumers localize error presentation at their boundary.

## Optional guarantees and future profiles

Deletion is capability gated and is **not implemented or advertised** here. There
is no delete Tool or empty provider method. Stable historical versions and retention
are explicitly false in this SDK/provider. Consumers must reject these stores for
durable immutable Snapshot use. Existence of a current version or a retry receipt
is not a conservation guarantee.

A future implemented profile can specify retention horizons, preparation/sealing,
cleanup, exact version conservation and matching provider interfaces. A future
query profile needs versioned schemas, both client/provider implementations and the
same immutable configured restrictions. No OData/GraphQL interface, query placeholder,
OpenDAL dependency, product resource redesign or automatic Agent publication is
part of this delivery.
