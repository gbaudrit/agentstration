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
read-only binding, and shuts down. It needs no network service, model, file store
or product host. Sample credentials are never printed. Running without `--demo`
serves an ephemeral in-process endpoint for the conformance host; the generated
credential is available only inside that process.

Implement `IAspObjectReader` and optionally `IAspObjectWriter`. Supply one or more
`AspToolBinding` instances to `AspMcpServer`; register with
`services.AddMcpServer().WithHttpTransport().WithAsp(server)` or use the explicit
handlers shown by the sample. The SDK publishes the standard schemas and maps
calls to provider methods. The host must authenticate MCP traffic and supply an
`AspAuthorize` callback that checks current product permissions on **every** call.
The callback returns trusted logical context, never context parsed from Tool
arguments. Set request-body limits in the transport host as shown by the sample.

A consumer supplies an already-authenticated `McpClient` to
`AspClient.ConnectAsync(mcp, toolPrefix, exactStoreRef)`. The client checks ASP
version/profile, schemas, fixed store, capabilities and optional exact provider
identity/version pins. It does not own the connection or automatically retry a
mutation. Keep the complete original write request for an exact bounded retry.

`MemoryObjectProvider` and its read-only adapter are executable provider examples,
not persistent storage. Their bounded process-local state never qualifies as
durable Snapshot storage. Physical backend configuration remains provider-owned;
this sample accepts the empty configuration object only.

To regenerate the checked-in wire schemas, pass an absolute output path (the .NET
runner uses the sample project as its working directory):

```sh
dotnet run --project samples/LocalStorage -- --schemas /absolute/path/to/asp/docs/schemas
```
