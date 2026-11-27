# AEP extension test isolation

Extension-owned tests compile in `tests/Agentstration.Extensions.<ExistingName>.Tests`.
Their project name preserves the extension project's spelling and casing. A focused
test run builds only that extension and its required SDK/contracts, rather than the
other extension executables or the product composition.

## Ownership inventory

The audit baseline is main `22f0cb2e`. Namespaces, test names, assertions and live
provider opt-in guards are preserved when sources move.

The Utilities follow-up starts from main `d12f7c23` after
[PR #1432](https://github.com/gbaudrit/agentstration/pull/1432). Its existing API
tests are consumer scenarios, so it adds native extension contracts rather than
moving those scenarios across their product boundary.

| Extension | Previous tests | Destination and dependencies |
| --- | --- | --- |
| Foundry | `ModelProviders.Tests/Foundry{Provider,Chat,Streaming,Live}Tests.cs` | `Extensions.Foundry.Tests`; Foundry, AEP abstractions/server, HTTP host testing |
| LocalAI | `ModelProviders.Tests/LocalAi{Provider,Integration}Tests.cs` | `Extensions.LocalAI.Tests`; LocalAI, AEP abstractions/server; private fake HTTP handlers |
| LlamaCpp | `ModelProviders.Tests/LlamaCpp{Provider,Integration}Tests.cs` | `Extensions.LlamaCpp.Tests`; LlamaCpp, AEP abstractions/server; private fake HTTP handlers |
| Ollama | Mapping in `ModelProviders.Tests/AepVerticalTests` and native streaming deadline in `InferenceTimeoutTests` | `Extensions.Ollama.Tests`; Ollama, AEP abstractions/server, OllamaSharp, `IChatClient` and HTTP host testing; capturing client moves with its sole consumer |
| Git | `SourceProviders.Git.Tests/GitSourceProviderTests.cs` | `Extensions.Git.Tests`; Git, AEP abstractions/server/client, HTTP host testing; existing private temporary repository fixture |
| OpenApi | `Extensions.OpenApi.Tests` | Existing project retained; OpenApi, AEP, Schemas, HTTP host testing |
| OpenDal | `Extensions.OpenDal.Tests` | Existing project retained; OpenDal, ASC client, HTTP host testing and native OpenDAL operators |
| PostgreSql | `Extensions.PostgreSql.Tests` | Existing project retained; PostgreSql, ASC structured-document sample, JSON fixtures and HTTP host testing |
| Crawl4AI | `Extensions.Crawl4AI.Tests` | Existing project retained; Crawl4AI, AEP, HTTP host testing |
| Utilities | `Api.Tests/McpToolCatalogTests.cs` (13 consumer cases) | Consumer cases retained; new `Extensions.Utilities.Tests` (15 cases) depends only on Utilities, AEP abstractions/client and HTTP host testing |

`ModelProviders.Tests` retains the consumer resolver, Value/Secret Binding,
diagnostics, protocol scenarios, two-hop inference deadlines and Worker/MAF
reasoning/knowledge propagation. These tests verify product or cross-module
behavior even when their names mention Ollama. Its Ollama reference remains
necessary, including the fake-provider AEP host rooted in the Ollama executable.
The Foundry, LocalAI and LlamaCpp project references are removed.

Architecture dependency checks, API Storage/Artifact scenarios, Management AEP
enrollment, automatic enrollment, Bootstrap and Connected Service scenarios stay
in their original families. Their extension references are intentional consumer
or composition boundaries, not extension-unit-test ownership.

No fixture library is introduced. Local fixtures move with their tests. The
existing Ollama deadline host/handler/stream fixture is extracted unchanged to
`tests/OllamaInferenceTestFixture.cs` and compiled by the dedicated and product
two-hop test projects; it contains no tests or product-module dependencies.
Existing shared product enrollment/API fixtures keep their boundaries. Extension
projects themselves depend on AEP/ASC and provider packages, not other extension
executables. The dedicated-test dependency contract traverses project references
to prevent that coupling from returning indirectly.

## Lanes and coverage

The four new model-extension projects stay in Integration and Coverage A, the
original lane/shard of their tests. Git's project is renamed in the root solution,
Integration and Coverage B. Fast retains its existing project inventory. All four
test solutions are checked together for disjoint functional lanes and exactly one
coverage shard per Integration project. No test class is compiled into two projects.

Utilities adds one project to the root solution, Integration and Coverage B.
Fast and Coverage A retain their inventories. Discovery grows from 5,026 to 5,041
cases (Fast 3,278 unchanged; Integration 1,748 to 1,763); coverage expects 54
logical module reports instead of 53. The 15 additions cover AEP identity and
contribution mappings, the MCP catalogue, SHA-256 known-vector/JSON/uppercase
results with and without dry-run, invariant casing, MCP error propagation, and
schema definition/instance validation. All 44 API cases, including the 13 product
MCP scenarios, retain their sources and assertions. No API project reference is
removed: those scenarios still need the Utilities host. No fixture library or
cross-project test source is added.

Live Foundry, LocalAI, LlamaCpp and PostgreSQL tests retain their existing
environment-variable prerequisites and report inconclusive in offline runs. They
do not require network access, a model, Azure or PostgreSQL by default.

## Packaging handoff

Only Foundry's image catalogue is updated here. It selects its entire dedicated
test directory and runs all offline classes, excluding the explicitly opt-in
`FoundryLiveTests`. The other image catalogues belong to their packaging PRs.
Until those catalogues declare their new test project, unclaimed paths continue
to select conservative product checks.

Pass these repository-relative project paths and filters to the packaging agents:

| Extension | `testProject` | `testFilter` |
| --- | --- | --- |
| Foundry | `tests/Agentstration.Extensions.Foundry.Tests/Agentstration.Extensions.Foundry.Tests.csproj` | `FullyQualifiedName!~FoundryLiveTests` |
| LocalAI | `tests/Agentstration.Extensions.LocalAI.Tests/Agentstration.Extensions.LocalAI.Tests.csproj` | `FullyQualifiedName!~LocalAiIntegrationTests` |
| LlamaCpp | `tests/Agentstration.Extensions.LlamaCpp.Tests/Agentstration.Extensions.LlamaCpp.Tests.csproj` | `FullyQualifiedName!~LlamaCppIntegrationTests` |
| Ollama | `tests/Agentstration.Extensions.Ollama.Tests/Agentstration.Extensions.Ollama.Tests.csproj` | `FullyQualifiedName~Agentstration.ModelProviders.Tests` |
| Git | `tests/Agentstration.Extensions.Git.Tests/Agentstration.Extensions.Git.Tests.csproj` | `FullyQualifiedName~GitSourceProviderTests` |
| Utilities | `tests/Agentstration.Extensions.Utilities.Tests/Agentstration.Extensions.Utilities.Tests.csproj` | `FullyQualifiedName~Agentstration.Extensions.Utilities.Tests` |

Existing OpenApi, OpenDal, PostgreSql and Crawl4AI project paths remain valid;
their catalogues and filters are unchanged. The Utilities follow-up leaves all
image catalogues unchanged; its packaging owner must select the new project and
filter above. Until a Utilities catalogue claims that directory, the classifier
conservatively selects product checks. Synthetic-catalogue regressions verify
the dedicated directory selects Utilities, mixed extension paths select their
images, and the API consumer path retains transverse checks. The classifier
implementation and the shared-project ownership regressions from #1431 remain
unchanged.

Ollama's packaging catalogue should also declare
`ci.sharedPaths: ["tests/OllamaInferenceTestFixture.cs"]`: this fixture is truly
shared with the product, so its changes must select Ollama and retain product
checks. This PR leaves that catalogue addition to the Ollama packaging owner.

Project isolation does not replace correct shared-project classification. Keep
the explicit-owner precedence and synthetic shared-project regressions from
[PR #1431](https://github.com/gbaudrit/agentstration/pull/1431) when synchronizing.
Dedicated directories select their own image, several owners select their images,
and genuinely shared fixtures, SDK inputs and mixed product paths retain transverse
validation. This remains catalogue-driven; no provider-specific classifier is added.
