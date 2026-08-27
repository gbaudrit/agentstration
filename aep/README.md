# Agentstration Extension Protocol (AEP)

AEP is an autonomous, versioned protocol and .NET SDK for discovering, validating, and consuming extensions. It has no dependency on the Agentstration product and can be built, tested, sampled, and inspected independently.

## Repository layout

- `src/`: protocol contracts, canonical client, ASP.NET Core server SDK, validation, and optional Microsoft.Extensions.AI adapter.
- `inspector/`: standalone protocol-driven Blazor Inspector.
- `cli/`: headless `inspect` and `validate` commands.
- `samples/`: provider-neutral extension examples.
- `tests/`: offline conformance and architectural tests.
- `docs/`: specification, concepts, and compatibility policy.

## Build and test

```powershell
dotnet restore Aep.slnx
dotnet build Aep.slnx --configuration Release --no-restore
dotnet test Aep.slnx --configuration Release --no-build
```

Run a generic sample and Inspector independently:

```powershell
dotnet run --project samples/HelloAepExtension
dotnet run --project inspector/Agentstration.Aep.Inspector.Web
```

Open the Inspector at `http://localhost:5190` and connect to `http://localhost:5200`.

Two executable capability samples exercise the interactive workbench without a provider dependency:

```powershell
dotnet run --project samples/ModelProviderExtension # http://localhost:5201
dotnet run --project samples/ToolsExtension         # http://localhost:5202
```

The Inspector provides:

- endpoint connection, health refresh and recent endpoints;
- manifest overview and capability-driven navigation;
- model/provider discovery and a chat playground with streaming, cancellation and generation options;
- AEP-to-MCP tool discovery, schema inspection, generated top-level forms, JSON mode and invocation;
- a bounded HTTP exchange viewer with redacted headers and JSON secrets;
- conformance validation and formatted raw payloads.

Extensions can publish immutable option-set versions and explicit directed migrations through `aep.configuration`. Consumers pin the option-set id, version, and schema digest; a newer preferred version does not silently reinterpret existing values. Migration requests validate every step before returning a new envelope. Secret annotations and schema-driven editing in the standalone Inspector remain future work.

## CLI

```powershell
dotnet run --project cli/Agentstration.Aep.Cli -- inspect http://localhost:5200
dotnet run --project cli/Agentstration.Aep.Cli -- validate http://localhost:5200 --format json
```

## Repository extraction

The `aep/` directory is deliberately self-contained and is ready to become its own Git repository. During the transition, Agentstration uses local project references into this directory. After package publication, those references should be replaced by versioned `PackageReference` entries. Official provider extensions remain outside this repository and consume the AEP SDK.

See [the protocol specification](docs/specification/protocol.md) and [compatibility policy](docs/compatibility/versioning.md).

## License

AEP, its .NET SDK, CLI, Inspector and samples are licensed under the [Apache License 2.0](LICENSE). See [NOTICE](NOTICE) for attribution information.
