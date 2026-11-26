# Agentstration Tracking Contract (ATC)

ATC defines provider-neutral tracking operations over MCP. Its exposure identity is
`agentstration.tracking.contracts.mcp.v1`, revision `1.0` (`ATC.mcp.v1`). A target can
be a repository, team, project or another configured tracker container. It does not
require a forge, an agent, an LLM or the Agentstration product.

The SDK contains independent `Agentstration.Atc.Abstractions`, `.Server` and `.Client`
packages. Optional `.Aep` declares the contract through existing AEP Tool metadata.
No package depends on Work, Coding, a vendor SDK, Runtime, EF or ASC/ASP. See the
[specification](docs/contract.md) and [OSLC fit assessment](docs/oslc-fit.md).

From the repository root, with the .NET SDK selected by `global.json`:

```sh
dotnet build atc/Atc.slnx -c Release
dotnet test --solution atc/Atc.slnx -c Release --no-build --minimum-expected-tests 55
dotnet run --project atc/samples/LocalTracking -c Release --no-build -- --demo
dotnet pack atc/src/Agentstration.Atc.Abstractions -c Release -o artifacts/atc
dotnet pack atc/src/Agentstration.Atc.Server -c Release -o artifacts/atc
dotnet pack atc/src/Agentstration.Atc.Client -c Release -o artifacts/atc
dotnet pack atc/src/Agentstration.Atc.Aep -c Release -o artifacts/atc
```

The demo uses a loopback-only, authenticated real HTTP MCP connection, creates a
non-repository tracking item, explicitly recovers its receipt, and lists the one
result. It creates ephemeral credentials without printing them. `--schemas` followed
by an absolute directory exports canonical input/output schemas. They are checked in
under `docs/schemas/` and generated from the shared executable contract.

Implement `IAtcProvider` and configure `AtcBinding` objects mapping exact logical
targets to provider instances. The host supplies a trusted per-call authorization
delegate and `AtcAccessContext`; business arguments cannot grant access. Register
`AtcMcpServer.ListToolsAsync` / `CallToolAsync` with the MCP server's normal handlers.
Enforce authenticated transport, request-body bounds and independent target access.
The sample shows ASP.NET Core hosting; the server adapter owns no transport.

Consumers supply an already-authenticated `McpClient` to `AtcClient.ConnectAsync`.
The typed client checks every required Tool's input/output schemas, declaration,
target, limits and capabilities. A cached description never replaces server-side
authorization. Client calls do not automatically retry writes or switch providers.
After uncertain execution, retain the original mutation and explicitly call recovery.

For an AEP host, add `AtcAepContributions.Create("tracker", "tracking")` to its existing
Tools and declare MCP server `tracking`. Each contribution contains the versioned
contract declaration and exact Tool mapping. Validate it with
`AtcAepContributions.Validate(manifest)` in addition to ordinary AEP validation.
`AtcAepContributions.Read(tool)` returns a validated declaration for client selection.
No new enrollment, generic registry or product resource is introduced.

The bounded memory provider is an executable example. Its receipt scope is **Process**,
not Durable. Host generations change on restart; old target references are refused.
Expired receipt keys remain uncertain and cannot become proof of absence. The sample
cannot establish vendor behavior or production durability. Product routing/adoption,
GitHub/GitLab and forge integration remain their separate issues (#955/#958/#959/#1379).
