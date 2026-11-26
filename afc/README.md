# Agentstration Forge Contract (AFC)

AFC defines provider-neutral forge operations over MCP. Its exposure identity is
`agentstration.forge.contracts.mcp.v1`, revision `1.0` (`AFC.mcp.v1`). It is independently
buildable without Work, Coding, a vendor account, an agent, Runtime, EF or an LLM.
ATC tracking, ASC storage and generic Git transport are separate contracts/capabilities.

The SDK supplies `Agentstration.Afc.Abstractions`, `.Server`, `.Client` and optional
`.Aep` packages. See the [normative specification](docs/contract.md). The AEP adapter
uses existing Tool contribution metadata, without introducing enrollment or a registry.

From the repository root, using the pinned .NET SDK:

```sh
dotnet build afc/Afc.slnx -c Release
dotnet test --solution afc/Afc.slnx -c Release --no-build --minimum-expected-tests 64
dotnet run --project afc/samples/LocalForge -c Release --no-build -- --demo
dotnet pack afc/src/Agentstration.Afc.Abstractions -c Release -o artifacts/afc
dotnet pack afc/src/Agentstration.Afc.Server -c Release -o artifacts/afc
dotnet pack afc/src/Agentstration.Afc.Client -c Release -o artifacts/afc
dotnet pack afc/src/Agentstration.Afc.Aep -c Release -o artifacts/afc
```

The authenticated loopback demo resolves exact fixture refs, creates/recovers/reads a
draft change request, and observes native review/check evidence. It associates an
opaque tracking identity from another provider without calling that provider or
acquiring tracking authority. Ephemeral credentials are not printed. `--schemas`
followed by an absolute directory exports the ten canonical input/output schemas.
The checked-in fixtures are generated from the executable contract.

Implement `IAfcProvider`, configure exact `AfcBinding` objects and supply a trusted
per-call authorization delegate. Register `AfcMcpServer.ListToolsAsync` and
`CallToolAsync` with the normal MCP server handlers. The host owns authenticated
transport, protected configuration and raw message/buffering bounds; business Tool
arguments cannot supply credentials or authorization. The sample shows HTTP hosting;
the server SDK does not own a transport or product process.

Consumers supply an authenticated, caller-owned `McpClient` to
`AfcClient.ConnectAsync`. The typed client verifies declaration/revision, actual Tool
mappings/schemas, target, capabilities and evidence. Retain the original create intent
and operation key after response loss; explicitly request recovery. There is no
automatic write retry or provider substitution. Recovery is historical effect evidence,
not a fresh proof that current refs, reviews or checks still satisfy a delivery policy.

For AEP, add `AfcAepContributions.Create("forge", "forge")` to existing Tools and
declare MCP server `forge`. Each contribution carries the versioned declaration and
exact mapping. Validate with `AfcAepContributions.Validate(manifest)` in addition to
ordinary AEP validation; `Read(tool)` supplies a checked client-selection declaration.
One extension/server may expose AFC together with unrelated contracts. AFC requires
neither an ATC contribution nor shared tracking/forge credentials.

The fixture provides atomic in-memory creation checks, bounded process-local receipts,
fresh binding generations on restart and explicit uncertain expiration. It cannot
establish native vendor atomicity or production durability. #951 owns Project
connection adoption, #958/#959 vendor adapters and #1349 delivery integration. Git
clone/fetch/commit/push remains #1344. This SDK does not merge change requests or
accept functional work, and it does not replace existing product orchestration.
