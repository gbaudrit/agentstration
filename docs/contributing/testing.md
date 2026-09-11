# Test lanes

Agentstration separates test execution by runtime cost and dependency type. The root `Agentstration.slnx` remains the complete build graph, while the checked-in test solutions provide explicit execution boundaries.

## Fast lane

Fast tests do not start an ASP.NET Core host, open a relational database, spawn a child process, or contact a remote provider. They cover architecture rules, component behavior, mapping, validation, and in-process command behavior.

```powershell
dotnet test --solution Agentstration.Tests.Fast.slnx --configuration Release --no-build --minimum-expected-tests 1
```

## Integration lane

Integration tests exercise a real boundary such as `WebApplicationFactory`, SQLite, Git, persistent Identity, or runtime reconstruction. This lane remains deterministic and offline by default. Tests marked `Integration` for a live model provider are opt-in and report inconclusive unless their documented environment variables are supplied.

```powershell
dotnet test --solution Agentstration.Tests.Integration.slnx --configuration Release --no-build --minimum-expected-tests 1
```

Run both fast and integration solutions for complete required functional validation. Their union is the functional test inventory represented by the root solution.

## Performance lane

Performance workloads live in a dedicated project and are never discovered by the fast or integration lanes:

```powershell
$env:AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER = "Sqlite"
$env:AGENTSTRATION_STORAGE_BENCHMARK_REPORT = "artifacts/storage-benchmark-sqlite.json"
dotnet test --solution Agentstration.Tests.Performance.slnx --configuration Release --no-build --filter TestCategory=Benchmark --minimum-expected-tests 1
```

The report records workload parameters, provider, elapsed time, runtime and OS metadata, throughput, median, p95, errors, conflicts, and retries. SQLite remains the local default; set `AGENTSTRATION_TEST_POSTGRES` for an opt-in PostgreSQL run. Pull requests execute only a bounded contention smoke for relevant storage paths. Full workloads run from the scheduled or manual `Storage performance` workflow and publish their JSON reports. Latency and throughput gates remain disabled until representative baselines have been collected; zero storage errors is always required.

## Project classification

| Test project | Lane | Boundary or rationale |
| --- | --- | --- |
| `Agentstration.ArchitectureTests` | Fast | Assembly dependency rules |
| `Agentstration.Management.Core.Tests` | Fast | Pure Management validation and in-memory use cases |
| `Agentstration.Tools.SourceRegistry.Tests` | Fast | In-process CLI and manifest validation |
| `Agentstration.Web.Components.Tests` | Fast | bUnit component behavior |
| `Agentstration.Web.FlowDesigner.Tests` | Fast | bUnit and graph projection behavior |
| `Agentstration.Workplace.Components.Tests` | Fast | bUnit component behavior |
| `Agentstration.Application.Tests` | Integration | Includes SQLite Flow and Work storage contracts |
| `Agentstration.Management.Storage.Tests` | Integration | SQLite control-plane, Identity persistence, secrets, audit, and trigger storage |
| `Agentstration.Management.Sources.Tests` | Integration | Source, registry, provider, and Pack distribution boundaries |
| `Agentstration.Management.Api.Tests` | Integration | Hosted Management API and declarative bootstrap tests using the API-only test profile |
| `Agentstration.Management.Security.Tests` | Integration | Identity, authorization, local-account, and interactive Security boundaries |
| `Agentstration.Management.Aep.Tests` | Integration | AEP enrollment lifecycle and extension inventory boundaries using the API-only test profile |
| `Agentstration.ModelProviders.Tests` | Integration, provider-optional | AEP test hosts plus opt-in live-provider checks |
| `Agentstration.Runtime.Tests` | Integration | SQLite reconstruction and hosted runtime endpoints |
| `Agentstration.SourceProviders.Git.Tests` | Integration | Real Git processes and file-system repositories |
| `Agentstration.Web.Tests` | Integration | Full Web host plus a bounded deterministic SQLite contention correctness smoke |
| `Agentstration.Performance.Tests` | Performance, opt-in | SQLite/PostgreSQL relational storage concurrency workloads and machine-readable reports |
| `Agentstration.Work.Api.Tests` | Integration | Full Work API host |
| `Agentstration.Workplace.Web.Tests` | Integration | Workplace HTTP host |

The autonomous AEP SDK keeps its own `aep/Aep.slnx` validation because it can be built and released independently from the product solution.
