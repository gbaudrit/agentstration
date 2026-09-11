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

Performance workloads are selected explicitly and are never part of the fast lane:

```powershell
$env:AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER = "Sqlite"
$env:AGENTSTRATION_STORAGE_BENCHMARK_REPORT = "artifacts/storage-benchmark-sqlite.json"
dotnet test --solution Agentstration.Tests.Performance.slnx --configuration Release --no-build --filter TestCategory=Benchmark --minimum-expected-tests 1
```

The current Web storage workload remains a mixed-project boundary until #303 moves it to a dedicated performance project. PostgreSQL is optional and runs only in its service-backed CI job or an explicitly configured local environment.

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
| `Agentstration.Management.Tests` | Integration, mixed | Remaining hosted API, Security, bootstrap, and AEP tests; split by #302 |
| `Agentstration.ModelProviders.Tests` | Integration, provider-optional | AEP test hosts plus opt-in live-provider checks |
| `Agentstration.Runtime.Tests` | Integration | SQLite reconstruction and hosted runtime endpoints |
| `Agentstration.SourceProviders.Git.Tests` | Integration | Real Git processes and file-system repositories |
| `Agentstration.Web.Tests` | Integration, mixed | Full Web host plus the temporary benchmark boundary tracked by #303 |
| `Agentstration.Work.Api.Tests` | Integration | Full Work API host |
| `Agentstration.Workplace.Web.Tests` | Integration | Workplace HTTP host |

The autonomous AEP SDK keeps its own `aep/Aep.slnx` validation because it can be built and released independently from the product solution.
