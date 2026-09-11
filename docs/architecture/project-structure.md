# Project structure

The solution groups domain contracts, use cases, adapters, storage, and hosts into explicit projects:

```text
src/
  Agentstration.Application/          Work and Workplace use cases
  Agentstration.Infrastructure/       Local adapters and composition support
  Agentstration.Management.*/         Management resources, use cases, SQLite
  Agentstration.Runtime.*/            Runtime contracts, core, local and MAF adapters
  Agentstration.Tools.SourceRegistry/ Offline Source Version validation and digest .NET tool
  Agentstration.Flow.*/               Flow model, use cases, contracts and SQLite
  Agentstration.Work*/                Work model, contracts, API and SQLite
  Agentstration.Console.*/            Operations Console typed clients and Razor presentation
  Agentstration.Api/                  Server REST, OpenAPI, MCP, SignalR and HTTP security transport
  Agentstration.Web*/                 Standalone composition root, Flow Designer and shared UI components
  Agentstration.Workplace.*/          End-user client, components and host
  Agentstration.AppHost/              Aspire development orchestration
tests/
  Agentstration.*.Tests/              MSTest behavior and architecture checks
docs/
  ...                                 Version-controlled product documentation
```

See [Architecture: current implementation](../architecture.md#solution-tree) for the complete project-by-project inventory.
