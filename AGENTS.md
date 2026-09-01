# AGENTS.md

## Purpose

This repository contains **Agentstration**, an open-source, self-hosted agent platform built with the Microsoft .NET AI stack. Its guiding principles are:

- Microsoft-first, provider-neutral, cloud-optional.
- A standalone, executable product before a distributed architecture.
- A modular monolith with explicit boundaries.
- Workspace isolation, durable execution, and safe local defaults.

These instructions apply to the entire repository.

## Start Here

Before making a non-trivial change, read:

- `README.md` for supported workflows and launch instructions.
- `docs/architecture.md` for boundaries, current flows, and planned increments.
- Relevant records in `docs/decisions/` for established architectural decisions.

Inspect the existing implementation before adding a new abstraction. This is an intentionally small MVP; extend the current verticals instead of creating parallel frameworks.

## Repository Map

```text
src/
  Agentstration.Application/     Work and Workplace use cases
  Agentstration.Flow/            Flow definitions, discriminated specs, references
  Agentstration.Flow.Application/
  Agentstration.Flow.Contracts/
  Agentstration.Flow.Storage.Abstractions/
  Agentstration.Flow.Storage.Sqlite/
  Agentstration.Infrastructure/  Composition adapters for current modules
  Agentstration.Management.Abstractions/ Canonical Management resources, ports, events
  Agentstration.Management.Core/ Management validation and use cases
  Agentstration.Management.Contracts/
  Agentstration.Management.Storage.Sqlite/
  Agentstration.Runtime.Abstractions/
  Agentstration.Runtime.AgentFramework/
  Agentstration.Runtime.Local/
  Agentstration.Work/             WorkItem aggregate and Runtime-facing port
  Agentstration.Work.Contracts/   Public Work Plane transport contracts
  Agentstration.Work.Storage.Abstractions/
  Agentstration.Work.Storage.Sqlite/
  Agentstration.Web/             REST, Razor Components, MCP, hosted workers
  Agentstration.AppHost/         Aspire orchestration and dashboard
tests/
  Agentstration.Application.Tests/
  Agentstration.ArchitectureTests/
  Agentstration.Management.Tests/
docs/
  architecture.md
  decisions/
```

## Architectural Rules

Preserve this dependency direction:

```text
Web -> Infrastructure -> Application -> Work
Web -> Management / Flow / Runtime public boundaries
```

- `Application` owns Work and Workplace use cases and provider-neutral ports. It must not depend on provider implementations.
- `Infrastructure` composes the current Management, Runtime, Flow, Work, identity, Pack, Trigger, Tool and provider adapters.
- `Web` is the composition and transport layer. REST endpoints, Razor components, hosted workers, and MCP tools must delegate to the same application services.
- Agentstration is the source of truth for agent definitions, immutable revisions, deployments, and desired state. Never persist a concrete `AIAgent`.
- `Agentstration.Management.Abstractions` owns canonical Management resources and provider-neutral ports; `Agentstration.Management.Core` owns Management validation and use cases. Do not place Management types back in the general Domain or Application projects.
- Concrete Microsoft Agent Framework types belong only in `Agentstration.Runtime.AgentFramework`.
- EF Core and SQLite control-plane implementation details belong only in `Agentstration.Management.Storage.Sqlite`.
- Work Plane EF Core and SQLite details belong only in `Agentstration.Work.Storage.Sqlite`; Work data must not use management or runtime storage.
- Flow domain types must remain provider-neutral. EF Core belongs only in `Agentstration.Flow.Storage.Sqlite`; Flow Core and Application must not reference Runtime, Web, MAF, or concrete storage.
- Published Flow versions are immutable. A WorkItem stores only a FlowReference, never an embedded FlowDefinition.
- `Agentstration.Work` owns functional work state and must delegate technical execution through `IWorkExecutionGateway`.
- Foundry must remain an optional runtime adapter and must not be referenced by Application, storage abstractions, or runtime abstractions.
- Do not put business logic in endpoints, Razor components, MCP tools, hosted workers, or `Program.cs`.
- Do not duplicate use cases for REST, UI, and MCP.
- Keep one deployable ASP.NET Core process. Do not introduce a microservice or external broker unless the task explicitly changes the architecture and an ADR records the decision.
- Add abstractions only when they provide a real testability or interchangeability boundary.

## Domain and Data Invariants

- Every workspace-owned entity and query must include `WorkspaceId`. Never retrieve an owned record by its entity ID alone.
- Use the strongly typed identifiers owned by each current module rather than passing bare `Guid` values across application boundaries.
- Use `TimeProvider` for testable time-dependent behavior; do not scatter `DateTimeOffset.UtcNow` through business code.
- Use `Result<T>` for expected business failures. Let unexpected infrastructure failures surface as exceptions and translate them at the transport boundary.

## Local-First Behavior

- The default configuration must run without Azure, a remote API key, PostgreSQL, Ollama, or another external service.
- Preserve `DeterministicChatClient` as the default automated-test and local fallback path.
- SQLite is the executable default for Management, Work, Flow, Runtime, identity and Trigger scheduling; neither PostgreSQL nor a remote provider may become mandatory.
- Provider-specific behavior belongs behind the existing Model Provider, Runtime, AEP, MCP and `IChatClient` boundaries.
- Centralize NuGet versions in `Directory.Packages.props`; do not put package versions in individual project files.

## Internationalization

- Every product-owned string visible to a user must be localized through `IStringLocalizer` and the appropriate RESX catalog. Do not hard-code UI labels, headings, descriptions, help text, status text, validation messages, accessibility labels, or action text in Razor, Razor Pages, or C#.
- English (`en-US`) is the neutral source and fallback culture. Every new or changed resource key must include a complete French (`fr-FR`) translation in the same change.
- Reuse a feature catalog or a shared UI catalog when ownership is clear; do not create a new catalog for isolated strings or place product UI translations in resource manifests.
- Keep API property names, resource identities, enum values, error codes, persisted values, protocol formats, and other technical identifiers invariant. Localize their presentation at the UI boundary.
- User-authored resource content is outside product RESX catalogs. Preserve the manifest source locale and use the localization-sidecar model defined for those resources when that vertical is available.
- Update localization rendering tests when observable text changes. Resource catalogs must remain valid, duplicate-free, and key-symmetric between the neutral and `fr-FR` files.

## Local Launch Profiles

The standard Development profiles use `deploy/bootstrap/profiles` as the bootstrap catalog, enable initial bootstrap, and apply its `development` profile to a fresh local instance with the public fixture account `admin / admin`. This credential is disposable local test data, not a secret, and must never be reused outside local Development.

For direct Web startup:

```powershell
# Bootstrap enabled; `http` is the default profile.
dotnet run --project src/Agentstration.Web
dotnet run --project src/Agentstration.Web --launch-profile https

# Bootstrap disabled.
dotnet run --project src/Agentstration.Web --launch-profile http-NoBootstrap
dotnet run --project src/Agentstration.Web --launch-profile https-NoBootstrap
```

For Aspire startup:

```powershell
# Bootstrap enabled; `https` is the default AppHost profile.
dotnet run --project src/Agentstration.AppHost

# Bootstrap disabled for the orchestrated Console.
dotnet run --project src/Agentstration.AppHost --launch-profile https-NoBootstrap
```

In Visual Studio, select the profile on the configured startup project. When `Agentstration.AppHost` is the startup project, its profile resolves and forwards the bootstrap catalog path, activation flag, and ordered initial profiles to the Console; profiles from `Agentstration.Web` do not appear in that selector.

Build configuration and host environment are independent. `--configuration Release` still uses the selected Development launch profile and does not disable bootstrap. Use a `NoBootstrap` profile or `--no-launch-profile` when initial bootstrap must be omitted intentionally. `NoBootstrap` controls `Agentstration:Bootstrap:InitialBootstrapEnabled`; it does not hide the catalog or erase `InitialProfiles`.

Catalog profiles may include a reserved `profile.yaml` with kind `BootstrapProfile` and `targetScope` `instance`, `tenant`, or `workspace`. Profiles applied together must use the same scope. Manual application is available to Platform administrators under **System > Bootstrap profiles** and requires an explicit Tenant or Workspace target for scoped profiles. Workspace profiles may directly create editable `ModelProvider`, `RuntimeProfile`, `ModelProfile`, `Agent`, `Flow`, and `Entry` resources; order dependencies before consumers. Keep Pack archives inside their profile and reference them with a relative `PackInstallation` path when Pack ownership and immutability are required; bootstrap installs a missing Pack but never replaces an installed version.

## C# Conventions

- Target .NET 10 and follow `.editorconfig` and `Directory.Build.props`.
- Nullable reference types, .NET analyzers, and warnings-as-errors are required.
- Prefer file-scoped namespaces, immutable records/value objects where appropriate, and constructor injection.
- Pass `CancellationToken` through every asynchronous I/O or long-running operation.
- Do not use service locators, global mutable state, or static accessors for application services.
- Validate configuration at startup and validate commands at application boundaries.
- Keep public contracts small and explicit. Avoid speculative interfaces, generic repositories, and unnecessary base classes.
- Use `System.Text.Json` for JSON unless a concrete compatibility requirement says otherwise.

## Security and Observability

- Never commit secrets, API keys, personal data, generated data stores, or real document contents used for testing.
- A credential may be committed only when it is explicitly documented as a public, disposable Development fixture, such as the local `admin / admin` bootstrap account. Never promote or reuse fixture credentials in an exposed or non-Development environment.
- Never log full documents, prompts, credentials, or sensitive agent output by default.
- Add structured correlation identifiers for relevant Workspace, WorkItem, FlowRun, Runtime Run and agent IDs.
- Enforce workspace access in every new REST, UI, MCP, persistence, and background-processing path.
- Use Problem Details for HTTP errors and `202 Accepted` for accepted asynchronous execution.
- Bound queues, payloads, file types, network calls, and agent execution where the affected path accepts untrusted input.

## Tests

Use MSTest. Add or update tests in the same change as behavior changes.

At minimum, cover the relevant combination of:

- Happy path and expected validation failures.
- Cross-workspace isolation.
- Idempotent execution events and background worker behavior.
- Deterministic routing and deterministic AI fallback behavior.
- Agent/tool failures, cancellation, and retry-safe state transitions.
- REST and MCP surfaces reusing application services.
- Project dependency rules when introducing or changing references.

Prefer in-memory infrastructure for fast application tests. Use integration infrastructure only when behavior genuinely depends on HTTP, the ASP.NET Core host, SQLite, or another real boundary. Tests must not require internet access or a live LLM.

## Validation Commands

Run from the repository root:

```powershell
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
dotnet test Agentstration.slnx --configuration Release --no-build
```

For a focused iteration, run the affected test project first, then run the full build and test suite before handoff. Do not suppress warnings or disable analyzers to make a change pass.

To smoke-test the executable default with the Development bootstrap when startup behavior changes:

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

Use `--launch-profile http-NoBootstrap` instead when the test must preserve or exercise a non-bootstrapped local state. Verify `/health` and the affected REST, Razor, or MCP path. Do not assume Docker, PostgreSQL, Ollama, or Aspire is available unless the task specifically targets that profile.

## Change Workflow

1. Inspect the affected vertical end to end: transport -> application/module service -> storage/runtime adapter -> tests.
2. Make the smallest coherent change that preserves existing boundaries and local defaults.
3. Add migrations when the relational model changes and keep alternative stores behaviorally aligned where applicable.
4. Update OpenAPI-facing contracts, MCP descriptions, UI behavior, and documentation when their observable behavior changes.
5. Add an ADR under `docs/decisions/` for a significant architectural choice; do not rewrite an accepted ADR to hide a new decision.
6. Build and test after meaningful increments, and report any validation that could not be run.

## Scope Discipline

The current repository is a foundation, not a production multi-tenant release. Durable management operations, manifest import, connection providers, traffic splitting, non-local hosting, Foundry bindings, runtime session storage, workload identities, and model-backed evaluation remain planned unless a task explicitly implements them. The SQLite/in-process/deterministic standalone path and default test suite must remain offline.

Do not convert planned capabilities into empty interfaces or placeholder projects. When implementing one, deliver a small executable vertical with storage, validation, tests, observability, and documentation appropriate to its risk.
