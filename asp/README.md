# Agentstration Storage Protocol (ASP)

ASP is a standalone bounded content/object profile over MCP. Its contracts, MCP
server and typed client have no dependency on the Agentstration product, AEP,
EF Core or a provider SDK. See [the specification](docs/protocol.md).

From this directory, using the .NET SDK selected by `global.json`:

```sh
dotnet build Asp.slnx -c Release
dotnet test --solution Asp.slnx -c Release --no-build
dotnet run --project samples/LocalStorage -c Release --no-build -- --demo
dotnet pack src/Agentstration.Asp.Abstractions -c Release -o artifacts
dotnet pack src/Agentstration.Asp.Server -c Release -o artifacts
dotnet pack src/Agentstration.Asp.Client -c Release -o artifacts
```

The demo starts a loopback-only MCP server, creates an ephemeral Bearer credential,
uses the real HTTP MCP client, writes JSON bytes, lists/stats/reads them, checks a
read-only target, and shuts down. It needs no network service, model, file store
or product host. Sample credentials are never printed. Running without `--demo`
serves an ephemeral in-process endpoint for the conformance host; the generated
credential is available only inside that process.

Implement `IAspObjectReader` and optionally `IAspObjectWriter`. Supply one or more
`AspStoreBinding` entries mapping logical targets to configured physical provider
instances and access policies. Register `AspMcpServer` using
`services.AddMcpServer().WithHttpTransport().WithAsp(server)` or the explicit handlers
shown by the sample. The SDK publishes one generic group of Tools, by default
`asp_describe/list/stat/read/write`, across all configured targets.

Every call carries `{ store: { storeId, bindingId, revision }, arguments: {...} }`.
An authenticated caller, including an authorized Agent, selects a target. The host
must check current permissions on **every** call; a selected ID is never authority.
The extension resolves the exact logical identity to its physical backend and
applies the target policy and current grant. Use transport request-body limits.
Physical configuration is opaque provider-defined JSON with the provider's own
schema/version; ASP does not impose a bucket/path/table model or configure backends
through content-call arguments. AEP/product integration owns that separate lifecycle.

A consumer supplies an already-authenticated `McpClient` to
`AspClient.ConnectAsync(mcp, "asp", selectedStoreRef)`. The typed client validates
ASP version/profile, schemas, target capabilities and optional exact provider pins,
then includes the selected target in every generic operation. It owns no transport
and does not retry mutations automatically. Keep original writes for bounded retries.

Exposing an Agentstration Tool that relays another Tool with fixed parameters is a
**general product capability**, outside this SDK. Such a relay can fix the ASP
`store` parameter while exposing operation arguments. The ASP SDK contains no
relay helper or Agent Tool publisher. Direct authorized generic calls remain valid.

`MemoryObjectProvider` and its read-only adapter are executable provider examples,
not persistent storage. Their bounded process-local state never qualifies as
durable Snapshot storage. Physical backend configuration remains provider-owned;
this sample accepts the empty configuration object only.

To regenerate the checked-in wire schemas, pass an absolute output path (the .NET
runner uses the sample project as its working directory):

```sh
dotnet run --project samples/LocalStorage -- --schemas /absolute/path/to/asp/docs/schemas
```
