# Agentstration

**The open-source, self-hosted control plane for AI agents.**

Agentstration lets you define governed agents, model profiles and tools, compose them into versioned Flows, distribute reusable Packs, and execute and track delegated work from an operations Console or the end-user Workplace.

It is built on the Microsoft .NET AI stack and currently executes agents through Microsoft Agent Framework (MAF), while keeping application contracts provider-neutral and cloud-optional. Real agents can run fully locally through Ollama, llama.cpp or LocalAI; no Azure subscription is required.

- **Website:** [www.agentstration.io/en](https://www.agentstration.io/en/)
- **Documentation:** [docs.agentstration.io](https://docs.agentstration.io/)
- **Project status:** public alpha, under active `0.x` development

## What is implemented

### Governed agent platform

- declarative, workspace-scoped Agents, model providers, Model Profiles, deployments, Extension registrations and tool catalogs;
- Ollama, llama.cpp and LocalAI integrations through autonomous, versioned AEP contributions;
- governed Tool execution with enablement checks, ordered hooks, workspace guards, human approval and durable audit records;
- local Secrets and Vault management;
- local accounts, external identity links, stable Principals, workspace memberships, scoped RBAC and security auditing.

### Flows and durable execution

- editable Flow drafts, immutable published versions and observable Flow Runs;
- structured Direct, Routing and Workflow Flows with typed steps and transitions;
- Microsoft Agent Framework orchestration modes, including Sequential, Concurrent, Handoff, Group Chat and Magentic;
- durable interactive execution for text, choice, confirmation and tool-approval requests;
- persisted checkpoints, selected revisions and run traces so supported executions can be reconstructed after a restart.

### Work Plane and Workplace

- durable Work Items, interactions, Tasks, Pending Actions, results, artifacts and notifications;
- Entries as governed user-facing access points to immutable Flow versions;
- workspace Dashboards that organize published Entries without exposing runtime details;
- a responsive, conversation-first Workplace that projects agent turns, progress, human input and outcomes;
- an operations Console for configuration, supervision, run inspection and governance.

### Packs and automation

- offline ZIP Pack installation with deterministic `publisher.name` namespaces and retained provenance;
- Pack inventory, resource bindings, exact-source forks, local authoring and builds, replacement and modification-safe uninstall;
- Pack Studio and workspace composition for ordinary Agentstration resources—Packs distribute resources but are never executed;
- workspace-scoped schedule Triggers supporting one-time, interval and Quartz cron schedules, IANA time zones, occurrence history, misfire/concurrency policies and `Run now`;
- Triggers submit autonomous Work to a Flow, including namespaced Flows installed by Packs. They do not introduce a second runtime.

## Architecture

Agentstration is a modular monolith with explicit **Management**, **Runtime**, **Work** and **Flow** boundaries:

- the **Management Plane** owns governed definitions and desired state;
- the **Runtime Plane** materializes and executes agents through provider-neutral contracts;
- the **Work Plane** receives, represents and tracks delegated work and its outcomes;
- the **Flow module** owns composition, publication, orchestration and durable Flow Runs.

Packs form a distribution layer above these boundaries. The repository produces multiple local hosts from one codebase: the operations Console and authoritative server, the standalone Workplace, the Work API and an Aspire AppHost. SQLite-backed stores keep the main module boundaries explicit.

The Agentstration Extension Protocol SDK, conformance validator, CLI, samples and standalone Inspector are staged autonomously in [`aep/`](aep/README.md). AEP gives extensions versioned discovery, capability and option contracts without leaking provider-specific concerns into portable Agentstration resources.

Read the [architecture overview](https://docs.agentstration.io/architecture/overview) and [current capabilities reference](https://docs.agentstration.io/reference/current-capabilities) for the detailed boundaries and guarantees.

## Quick start

### Requirements

- the .NET SDK selected by [`global.json`](global.json), currently .NET SDK 10.0.300 with compatible feature-band roll-forward;
- Ollama, llama.cpp or LocalAI to execute real agents locally;
- optionally Docker for container-based local model or Compose workflows.

### Run locally

```powershell
git clone https://github.com/gbaudrit/agentstration.git
cd agentstration

dotnet run --project src/Agentstration.Web
```

Open the operations Console at [http://localhost:5100](http://localhost:5100). In local Development, the default `http` and `https` launch profiles enable the `development` profile from the versioned bootstrap catalog and create the public fixture `admin / admin`, Tenant `dev`, and Workspace `default` on a fresh instance.

Use `--launch-profile http-NoBootstrap` or `--launch-profile https-NoBootstrap` to start Development without applying initial profiles. These launch profiles retain the catalog path and selected profiles but set `InitialBootstrapEnabled` to `false`. Published applications, Production, and runs using `--no-launch-profile` do not activate the Development profile. Without declarative bootstrap, `/bootstrap` remains available to create the first global local administrator plus the initial Tenant and Workspace interactively.

When `Agentstration.AppHost` is the Visual Studio startup project, select its `https` profile for the default bootstrap or `https-NoBootstrap` to disable it for the orchestrated Console resource.

After initialization, a Platform administrator can open **System > Bootstrap profiles** to compose profiles in a defined order, preview every create, skip, conflict, or validation error, select an explicit Tenant or Workspace target when required, and confirm the application. Manual applications are retained in durable history. A profile declares its scope in a reserved `profile.yaml`:

```yaml
apiVersion: agentstration.io/v1
kind: BootstrapProfile
metadata:
  name: workspace-tools
definition:
  displayName: Workspace tools
  description: Reusable tools and agents for one Workspace
  targetScope: workspace
  bindings:
    - name: agent-model
      targetKind: modelProfile
      displayName: Agent model
      description: Model Profile selected for the reusable agents
      required: true
```

Workspace profiles can declare typed bindings so their ordinary editable resources do not embed environment-specific names. The Console asks for each target before preview; API callers provide the same profile-qualified selections. A binding may define `defaultTarget` for non-interactive use. Only a structured reference object is substituted, never arbitrary YAML text:

```yaml
definition:
  displayName: Support agent
  instructions: Answer support questions concisely.
  modelProfile:
    binding: agent-model
  runtimeProfile:
    name: maf-builtin
    namespace: default
```

Selections are included in the preview digest and retained as resource references in application history. They may target an existing resource or one planned earlier in the same composition. Secret bindings retain only the Secret reference; secret values are never copied into the profile, preview, or history.

A Workspace profile can install an existing local Pack while preserving Pack ownership and immutability. The archive path is relative to the profile directory; HTTP sources and replacement of an installed Pack are intentionally rejected:

```yaml
apiVersion: agentstration.io/v1
kind: PackInstallation
metadata:
  name: standard-tools
definition:
  source:
    path: artifacts/standard-tools.zip
  bindings: []
```

A Workspace profile may also create ordinary `ModelProvider`, `RuntimeProfile`, `ModelProfile`, `Agent`, `Flow`, and `Entry` resources directly from their normal YAML manifests. Unlike resources installed from a Pack, these resources have no Pack provenance and remain editable through their usual Console and API surfaces. Files and YAML documents are evaluated in lexical order, so dependencies must precede their consumers: provider and runtime profile, then model profile, agent, flow, and entry. A Model Provider must reference an Extension Registration already available in the target Workspace. A published Entry targeting a Flow requires that Flow to already have, or create earlier in the same application, an active published version.

For example, this creates an editable Agent using resources declared earlier in the same profile:

```yaml
apiVersion: agentstration.io/v1
kind: Agent
metadata:
  name: support-agent
definition:
  displayName: Support agent
  instructions: Answer support questions concisely.
  modelProfile:
    name: support-model
  runtimeProfile:
    name: local-runtime
  tools: []
```

In the Development environment, the complete interactive HTTP API reference is available at [http://localhost:5100/swagger](http://localhost:5100/swagger), backed by the OpenAPI document at [http://localhost:5100/openapi/v1.json](http://localhost:5100/openapi/v1.json). Swagger supports the current Console session cookie and JWT bearer tokens; SignalR and MCP remain separate transports.

`Managed` is the normal execution mode. Configure an Ollama, llama.cpp or LocalAI provider and bind a Model Profile to run real agents entirely on your machine. The provider endpoint and selected model are resolved from Agentstration's persisted resources.

For a first UI exploration, automated test or diagnostic session without any model, use the deterministic fallback:

```powershell
$env:AI__Provider = "Deterministic"
dotnet run --project src/Agentstration.Web
```

Deterministic mode produces reproducible simulated responses. It is not a substitute for a local model and is not the normal production path.

To run the end-user Workplace, keep the authoritative server running and start a second terminal:

```powershell
dotnet run --project src/Agentstration.Workplace.Web
```

Open [http://localhost:5180](http://localhost:5180). The Workplace API defaults to the server at `http://localhost:5100`.

For Aspire orchestration and its local dashboard:

```powershell
dotnet run --project src/Agentstration.AppHost
```

Or use Compose:

```powershell
docker compose up --build
```

Aspire starts Agentstration and its AEP extensions, but does not install inference servers or download models. Follow the [local installation guide](https://docs.agentstration.io/getting-started/local-installation) and [model provider guide](https://docs.agentstration.io/concepts/model-providers) for provider-specific setup.

## Build and test

```powershell
dotnet build Agentstration.slnx --configuration Release
dotnet test Agentstration.slnx --configuration Release
```

Warnings are treated as errors, .NET analyzers are enabled and NuGet audit findings fail restore. The default tests are designed to remain offline and cost-free; real-provider smoke tests are opt-in.

## Documentation

The published documentation at [docs.agentstration.io](https://docs.agentstration.io/) tracks the current development branch and covers:

- [getting started](https://docs.agentstration.io/getting-started/overview);
- [concepts](https://docs.agentstration.io/concepts/overview);
- [architecture](https://docs.agentstration.io/architecture/overview);
- [reference](https://docs.agentstration.io/reference/overview);
- [Architecture Decision Records](https://docs.agentstration.io/decisions);
- [contributor guidance](https://docs.agentstration.io/contributing/overview).

The Markdown and MDX files under `docs/` are the source of truth; `docs/site/` contains the Docusaurus renderer. To work on the site locally, follow [Working on the documentation](docs/contributing/documentation.md).

## Project status

Agentstration is a **public alpha** under active `0.x` development. Public APIs, resource contracts and package formats may still change. It is a product foundation, not yet a production multi-tenant release; planned capabilities and current limits are identified explicitly in the documentation.

Semantic Versioning is the intended product-versioning policy, but the repository does not yet publish a product version or release tags.

## License

Agentstration is licensed under the [Apache License 2.0](LICENSE). The license includes an explicit patent grant; trademarks and product names are not licensed except as required for customary attribution. See [NOTICE](NOTICE) for attribution information.

## Contributing

Contributions are welcome. Read [CONTRIBUTING.md](CONTRIBUTING.md), the [Code of Conduct](CODE_OF_CONDUCT.md) and the [Security policy](SECURITY.md) before opening a substantial change.
