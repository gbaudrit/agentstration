# AGENTS.md

## Purpose

This repository contains **Agentstration**, an open-source, self-hosted agent platform built with the Microsoft .NET AI stack. Its guiding principles are:

- Microsoft-first, provider-neutral, cloud-optional.
- A standalone, executable product before a distributed architecture.
- A modular monolith with explicit boundaries.
- Raw source preservation, workspace isolation, and safe local defaults.

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
  Agentstration.Domain/          Entities, value types, typed IDs, domain events
  Agentstration.Contracts/       Transport-neutral request/response contracts
  Agentstration.Application/     Use cases, ports, routing, workflows
  Agentstration.Evaluation/      Offline AI workflow quality evaluators
  Agentstration.Flow/            Flow definitions, discriminated specs, references
  Agentstration.Flow.Application/
  Agentstration.Flow.Contracts/
  Agentstration.Flow.Storage.Abstractions/
  Agentstration.Flow.Storage.Sqlite/
  Agentstration.Infrastructure/  Storage, AI, HTTP, events, queues, tools
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
  Agentstration.Evaluation.Tests/
  Agentstration.Management.Tests/
docs/
  architecture.md
  decisions/
```

## Architectural Rules

Preserve this dependency direction:

```text
Web -> Infrastructure -> Application -> Contracts -> Domain
Web --------------------> Application -------------> Domain
```

- `Domain` must remain framework-agnostic. Do not reference EF Core, ASP.NET Core, MCP, Microsoft Agent Framework, or concrete AI/storage providers from it.
- `Application` owns use cases and provider-neutral ports. It may use `Microsoft.Extensions.AI` contracts, but must not depend on provider implementations.
- `Infrastructure` implements application ports and contains EF Core, JSON storage, AI adapters, URL fetching, queues, and event-bus implementations.
- `Web` is the composition and transport layer. REST endpoints, Razor components, hosted workers, and MCP tools must delegate to the same application services.
- Agentstration is the source of truth for agent definitions, immutable revisions, deployments, and desired state. Never persist a concrete `AIAgent`.
- `Agentstration.Management.Abstractions` owns canonical Management resources and provider-neutral ports; `Agentstration.Management.Core` owns Management validation and use cases. Do not place Management types back in the general Domain or Application projects.
- Concrete Microsoft Agent Framework types belong only in `Agentstration.Runtime.AgentFramework`.
- EF Core and SQLite control-plane implementation details belong only in `Agentstration.Management.Storage.Sqlite`.
- Work Plane EF Core and SQLite details belong only in `Agentstration.Work.Storage.Sqlite`; Work data must not use management or runtime storage.
- Flow domain types must remain provider-neutral. EF Core belongs only in `Agentstration.Flow.Storage.Sqlite`; Flow Core and Application must not reference Runtime, Web, MAF, or concrete storage.
- Published Flow versions are immutable. A WorkItem stores only a FlowReference, never an embedded FlowDefinition.
- `Agentstration.Work` owns functional work state and must delegate technical execution through `IWorkExecutionGateway`.
- Foundry must remain an optional runtime adapter and must not be referenced by Domain, Application, storage abstractions, or runtime abstractions.
- Do not put business logic in endpoints, Razor components, MCP tools, hosted workers, or `Program.cs`.
- Do not duplicate use cases for REST, UI, and MCP.
- Keep one deployable ASP.NET Core process. Do not introduce a microservice or external broker unless the task explicitly changes the architecture and an ADR records the decision.
- Add abstractions only when they provide a real testability or interchangeability boundary.

## Domain and Data Invariants

- Every workspace-owned entity and query must include `WorkspaceId`. Never retrieve an owned record by its entity ID alone.
- Preserve raw ingested content exactly. Normalization, summaries, categories, and agent output belong in separate records and must never overwrite the source.
- Keep ingestion idempotent within `(WorkspaceId, InboxId, ContentHash)`.
- Publish the corresponding domain event after a successful state transition.
- Use strongly typed identifiers already defined in `Agentstration.Domain` rather than passing bare `Guid` values across application boundaries.
- Use `TimeProvider` for testable time-dependent behavior; do not scatter `DateTimeOffset.UtcNow` through business code.
- Use `Result<T>` for expected business failures. Let unexpected infrastructure failures surface as exceptions and translate them at the transport boundary.
- Keep the deterministic router stateless. It may inspect memory through application contracts but does not own persistence.

## Local-First Behavior

- The default configuration must run without Azure, a remote API key, PostgreSQL, Ollama, or another external service.
- Preserve `DeterministicChatClient` as the default automated-test and local fallback path.
- SQLite is the executable control-plane default. The existing content/memory vertical continues to use the local JSON store by default; neither PostgreSQL nor a remote provider may become mandatory.
- Provider-specific behavior belongs behind existing application interfaces such as `IPlatformStore`, `IAgentRuntime`, `IChatClient`, `IContentSourceReader`, and `IObservationTool`.
- Centralize NuGet versions in `Directory.Packages.props`; do not put package versions in individual project files.

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
- Never log full documents, prompts, credentials, or sensitive agent output by default.
- Add structured correlation identifiers for relevant `WorkspaceId`, `ItemId`, `MissionId`, and agent/run IDs.
- Treat URL ingestion as an SSRF-sensitive boundary. Preserve scheme validation, DNS/IP checks, timeouts, streaming, cancellation, and payload limits; apply equivalent checks to redirects when adding redirect support.
- Enforce workspace access in every new REST, UI, MCP, persistence, and background-processing path.
- Use Problem Details for HTTP errors and return `202 Accepted` for queued ingestion.
- Bound queues, payloads, file types, network calls, and agent execution where the affected path accepts untrusted input.

## Tests

Use MSTest. Add or update tests in the same change as behavior changes.

At minimum, cover the relevant combination of:

- Happy path and expected validation failures.
- Cross-workspace isolation.
- Ingestion idempotency and exact raw-content preservation.
- Domain-event publication and background workflow behavior.
- Deterministic routing and deterministic AI behavior.
- Agent/tool failures, cancellation, and retry-safe state transitions.
- REST and MCP surfaces reusing application services.
- Project dependency rules when introducing or changing references.

Prefer in-memory infrastructure for fast application tests. Use integration infrastructure only when the behavior genuinely depends on PostgreSQL, HTTP, the ASP.NET Core host, or another real boundary. Tests must not require internet access or a live LLM.

## AI Evaluation

Use `Microsoft.Extensions.AI.Evaluation` for quality evaluation of agent and workflow outputs. Evaluation is distinct from ordinary unit and integration testing: deterministic tests verify application behavior, while evaluation scenarios measure the quality of non-deterministic AI output.

- Keep evaluation orchestration outside `Domain`. Evaluators, datasets, model configuration, and reporting belong in a dedicated evaluation test project or infrastructure adapter.
- Exercise agents through the same `IAgentRuntime` and `IChatClient` boundaries used by the application. Do not create an evaluation-only implementation of business workflows.
- The first required scenario is the content workflow: normalize source content, generate a summary and categories, then evaluate the result against the preserved source.
- Cover relevance, completeness, source fidelity/groundedness, summary quality, category quality, stability, and safety as applicable to the scenario.
- Include deterministic assertions for invariants that must never depend on an evaluator, such as non-empty output, raw-content preservation, workspace isolation, valid result structure, and absence of fabricated source mutations.
- Store evaluation cases as small, sanitized, version-controlled datasets. Each case should identify its source, task, expected characteristics, and applicable evaluators without containing secrets, personal data, or proprietary documents.
- Record enough metadata to reproduce and compare a run: scenario and dataset version, prompt/workflow version, provider, model/deployment name, evaluator configuration, and timestamp. Never record API keys or full sensitive prompts in reports or telemetry.
- Define explicit pass thresholds near the scenario. Do not weaken thresholds merely to accept a regression; document intentional threshold changes and explain the quality tradeoff.
- Keep the default build and test suite deterministic, offline, and free of remote API requirements. Model- or judge-backed evaluations must be opt-in and clearly labeled unless a deterministic fake evaluator is used.
- Separate smoke evaluations from broader benchmark suites. CI may run fast deterministic evaluation checks by default; remote, costly, or statistically repeated suites should run in an explicitly configured job.
- When measuring stability, execute multiple samples where the provider permits it and report the distribution or failure rate rather than relying on one favorable output.
- Treat evaluator output as diagnostic evidence, not domain truth. Evaluation failures must not modify production memory, missions, notifications, or source content.
- Emit evaluation telemetry without document bodies or sensitive prompts. Correlate results with scenario, evaluator, model, workflow version, and agent/run identifiers where available.
- Add `Microsoft.Extensions.AI.Evaluation` package versions centrally in `Directory.Packages.props`. Keep package-specific APIs behind the evaluation boundary so upgrades do not leak into domain or application contracts.

When an AI workflow or prompt changes, update its deterministic tests and relevant evaluation scenarios together. Report which evaluation suite was run, its provider/model, and any skipped opt-in evaluation.

## Validation Commands

Run from the repository root:

```powershell
dotnet restore Agentstration.slnx
dotnet build Agentstration.slnx --configuration Release --no-restore
dotnet test Agentstration.slnx --configuration Release --no-build
```

For a focused iteration, run the affected test project first, then run the full build and test suite before handoff. Do not suppress warnings or disable analyzers to make a change pass.

To smoke-test the executable default when startup behavior changes:

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

Verify `/health` and the affected REST, Razor, or MCP path. Do not assume Docker, PostgreSQL, Ollama, or Aspire is available unless the task specifically targets that profile.

## Change Workflow

1. Inspect the affected vertical end to end: transport -> application service -> domain -> infrastructure -> tests.
2. Make the smallest coherent change that preserves existing boundaries and local defaults.
3. Add migrations when the relational model changes and keep alternative stores behaviorally aligned where applicable.
4. Update OpenAPI-facing contracts, MCP descriptions, UI behavior, and documentation when their observable behavior changes.
5. Add an ADR under `docs/decisions/` for a significant architectural choice; do not rewrite an accepted ADR to hide a new decision.
6. Build and test after meaningful increments, and report any validation that could not be run.

## Scope Discipline

The current repository is a foundation, not a production multi-tenant release. Durable management operations, manifest import, connection/identity providers, traffic splitting, non-local hosting, Foundry bindings, runtime session storage, authentication, and the model-backed `Microsoft.Extensions.AI.Evaluation` suite remain planned unless a task explicitly implements them. The SQLite/in-process/deterministic standalone vertical and deterministic evaluation baseline are part of the default test suite and must remain offline.

Do not convert planned capabilities into empty interfaces or placeholder projects. When implementing one, deliver a small executable vertical with storage, validation, tests, observability, and documentation appropriate to its risk.
