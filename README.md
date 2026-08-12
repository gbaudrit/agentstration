# Agentstration

Agentstration is an open-source, self-hosted platform for governing, executing, and tracking work delegated to agents. It is built on the Microsoft .NET AI stack while remaining provider-neutral and cloud-optional.

Agentstration currently provides:

- declarative agents, model/tool providers, governed tool catalogs, profiles, and deployments;
- durable Work Items, Workplace interactions, tasks, results, and artifacts;
- editable Flows, immutable published Flow versions, and observable Flow Runs;
- persisted tenants, workspaces, users, memberships, scoped RBAC, and automatic standalone bootstrap;
- REST, Razor/Blazor, MCP, and local runtime surfaces backed by shared application services;
- SQLite and deterministic local defaults that require no Azure subscription or remote API key.

The product is a modular monolith organized around a Management Plane, Runtime Plane, and Work Plane. See the [architecture overview](docs/architecture/overview.md) for the boundaries and dependency rules.

The autonomous Agentstration Extension Protocol SDK, conformance validator, CLI, samples, and standalone Inspector are staged in [`aep/`](aep/README.md). That directory has its own solution and build configuration so it can be moved into a dedicated repository without carrying Agentstration application projects.

## Quick start

Requirements: the .NET SDK version selected by [`global.json`](global.json) (currently .NET 10.0.300 or a compatible feature band).

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

Open the operations Console at `http://localhost:5100`. A fresh standalone installation automatically creates the local organization, default workspace, and Local User with tenant-level Owner access.

For the standalone end-user Workplace and its Work API, follow the [local installation guide](docs/getting-started/local-installation.md).

## Documentation

- [Documentation portal source](docs/index.md)
- [Getting started](docs/getting-started/overview.md)
- [Concepts](docs/concepts/overview.md)
- [Architecture](docs/architecture/overview.md)
- [Reference](docs/reference/overview.md)
- [Architecture decisions](docs/decisions/index.md)
- [Detailed current capabilities](docs/reference/current-capabilities.md)

Run the documentation site locally with `npm install` and `npm start` from `docs/site`. The complete workflow is documented in [Working on the documentation](docs/contributing/documentation.md).

## Project status

Agentstration is under active `0.x` development. Public contracts can still evolve, and planned capabilities are identified explicitly in the documentation. Semantic Versioning is the intended product-versioning policy; the repository does not yet publish a product version or release tags.

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), the [Code of Conduct](CODE_OF_CONDUCT.md), and the [Security policy](SECURITY.md) before opening a substantial change.
