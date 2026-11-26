# Agentstration Storage Contract (ASC)

The current exposure is `agentstration.storage.contracts.mcp.v1` (`ASC.mcp.v1`),
contract revision `1.0` over MCP `2026-07-28`. AEP Storage Type versions explicitly
declare these identities alongside their operation profile and provider pins.
See the [identity replacement inventory](../docs/reference/asc-identity-migration.md).

ASC is a standalone bounded content/object profile over MCP. Its contracts, MCP
server and typed client have no dependency on the Agentstration product, AEP,
EF Core or a provider SDK. The optional `Agentstration.Asc.Aep` package adds AEP
configuration integration without adding product-resource dependencies. See [the specification](docs/protocol.md).

From this directory, using the .NET SDK selected by `global.json`:

```sh
dotnet build Asc.slnx -c Release
dotnet test --solution Asc.slnx -c Release --no-build
dotnet run --project samples/LocalStorage -c Release --no-build -- --demo
dotnet pack src/Agentstration.Asc.Abstractions -c Release -o artifacts
dotnet pack src/Agentstration.Asc.Server -c Release -o artifacts
dotnet pack src/Agentstration.Asc.Client -c Release -o artifacts
```

The demo starts a loopback-only MCP server, creates an ephemeral Bearer credential,
uses the real HTTP MCP client, demonstrates complete and progressive text/binary transfers, a byte range, lists/stats, checks a
read-only target, and shuts down. It needs no network service, model, file store
or product host. Sample credentials are never printed. Running without `--demo`
serves an ephemeral in-process endpoint for the conformance host; the generated
credential is available only inside that process.

Implement `IAscObjectReader` and optionally `IAscObjectWriter`. Supply one or more
`AscStoreBinding` entries mapping logical targets to configured physical provider
instances and access policies. Register `AscMcpServer` using
`services.AddMcpServer().WithHttpTransport().WithAsc(server)` or the explicit handlers
shown by the sample. The SDK publishes one generic group of Tools, by default
`asc_describe/list/stat`, `asc_read_text/blob`, `asc_write_text/blob`,
and session close/abort/status operations, across all configured targets.

Every call carries `{ store: { storeId, bindingId, revision }, arguments: {...} }`.
An authenticated caller, including an authorized Agent, selects a target. The host
must check current permissions on **every** call; a selected ID is never authority.
The extension resolves the exact logical identity to its physical backend and
applies the target policy and current grant. Use transport request-body limits.
Physical configuration is opaque provider-defined JSON with the provider's own
schema/version; ASC does not impose a bucket/path/table model or configure backends
through content-call arguments. AEP/product integration owns that separate lifecycle.

For dynamically configured AEP hosts, the optional `Agentstration.Asc.Aep` package
provides `AscAepBindings` and `MapAscAepBindings()`. Publish immutable
`aep.storage-types` contributions and implement `IAscAepBindingFactory` to validate
your opaque configuration/envelope and authorize the actual authenticated caller.
Use the dynamic `AscMcpServer` binding resolver with `AscAepBindings.ResolveAsync`.
The bounded authenticated configuration endpoint issues short-lived, subject-bound
leases selected through a protected HTTP header. Content arguments cannot configure
targets. Effective policies may narrow requested rights and bounds; they cannot
widen them. Each operation still requires independent provider authorization.
See the [product integration contract](../docs/reference/storage-execution.md#aepasc-extension-contract)
for composition and credential handling.

A consumer supplies an already-authenticated `McpClient` to
`AscClient.ConnectAsync(mcp, "asc", selectedStoreRef)`. The typed client validates
ASC version/profile, schemas, target capabilities and optional exact provider pins,
then includes the selected target in every generic operation. It owns no transport
and does not retry mutations automatically. Keep original writes for bounded retries.

Exposing an Agentstration Tool that relays another Tool with fixed parameters is a
**general product capability**, outside this SDK. Such a relay can fix the ASC
`store` parameter while exposing operation arguments. The ASC SDK contains no
relay helper or Agent Tool publisher. Direct authorized generic calls remain valid.

`MemoryObjectProvider` and its read-only adapter are executable provider examples,
not persistent storage. Their bounded process-local state never qualifies as
durable Snapshot storage. Physical backend configuration remains provider-owned;
this sample accepts the empty configuration object only.

To regenerate the checked-in wire schemas, pass an absolute output path (the .NET
runner uses the sample project as its working directory):

```sh
dotnet run --project samples/LocalStorage -- --schemas /absolute/path/to/asc/docs/schemas
```


Providers expose streams through `OpenReadAsync` and `OpenWriteAsync`; the SDK
owns bounded protocol sessions and replay receipts. Small transfers complete in
one call. Larger transfers continue through the same text or binary method with
`sessionRef` and the next sequence. `AscStreams.OpenReadStream/OpenWriteStream`
provide async Stream adapters; writes require explicit `CompleteAsync`.
Capabilities, conditional writes, optional metadata, chunk/range semantics and
uncertain outcomes are specified in [the protocol](docs/protocol.md).

## Structured document and query profiles

`asc.documents/1` is an additive MCP mapping in the same standalone SDK. It supplies
conditional document operations, open versioned query-profile references,
protected cursors and optional immutable publication. `Agentstration.Asc.OData`
contains the optional bounded standard OData parser/model adapter; generic ASC
contracts do not depend on it or on PostgreSQL. The memory sample is
`asc/samples/StructuredDocuments`; the product's autonomous PostgreSQL provider is
`src/extensions/Agentstration.Extensions.PostgreSql`. See the full
[contract and deployment reference](../docs/reference/structured-documents.md).

Protected document bindings report bounded problem codes for backend connection refusal, unavailability and timeout (`backend_connection_refused`, `backend_unavailable`, `backend_timeout`). These remain distinct from policy refusal. PostgreSQL classifies connection failures before returning the binding response; product acquisitions retain the reason and localize it without forwarding connection strings or remote exception text. Unknown or malformed peer failures fall back to the HTTP status category.
