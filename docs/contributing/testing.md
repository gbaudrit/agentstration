# Test lanes

Agentstration separates test execution by runtime cost and dependency type. The root `Agentstration.slnx` remains the complete build graph, while the checked-in test solutions provide explicit execution boundaries.

## Fast lane

Fast tests do not start an ASP.NET Core host, open a relational database, spawn a child process, or contact a remote provider. They cover architecture rules, component behavior, mapping, validation, and in-process command behavior.

```powershell
dotnet test --solution Agentstration.Tests.Fast.slnx --configuration Release --no-build --minimum-expected-tests 777 --max-parallel-test-modules 4
```

## Integration lane

Integration tests exercise a real boundary such as `WebApplicationFactory`, SQLite, Git, persistent Identity, or runtime reconstruction. This lane remains deterministic and offline by default. Tests marked `Integration` for a live model provider are opt-in and report inconclusive unless their documented environment variables are supplied.

```powershell
dotnet test --solution Agentstration.Tests.Integration.slnx --configuration Release --no-build --minimum-expected-tests 954 --max-parallel-test-modules 2
```

Run both fast and integration solutions for complete required functional validation. Their union is the functional test inventory represented by the root solution.

API integration tests run in four independent test assemblies: `Agentstration.Api.Flows.Tests`, `Agentstration.Api.Execution.Tests`, `Agentstration.Api.Resources.Tests`, and `Agentstration.Api.Tests`. These preserve the former consolidated module's cases and test names. The Resources module also owns the 38 Data Source and Artifact API cases moved from Management API after the combined 142-case module exceeded its retained memory budget (2725.1 MiB on Ubuntu CI). Each assembly owns its fixtures and runs its cases sequentially. Memory budgets remain unchanged; diagnostic minimum counts are 118 for Resources and 104 for Management API. The diagnostic job runs these assemblies one after another in separate processes so process memory is released between groups. The Integration lane and coverage shard B include all four projects.

### SQLite connection pooling regression

`SqliteConnectionPoolTests` in `Agentstration.Management.Storage.Tests` exercises concurrent pooled opens with active readers. Sixteen dedicated workers check that live logical connections retain distinct native handles and independent query results across 500 alternating cold and warm waves. The test uses its own temporary database, has a bounded execution time, and clears only that database's pool between cold waves and during cleanup. Its test output records the loaded managed driver and native SQLite versions.

The central `Microsoft.Data.Sqlite.Core` pin includes the upstream pooled-connection ownership correction from 10.0.13 independently of the EF Core version. Keep the SQLitePCLRaw bundle compatible with the driver. For a focused check after the Release build:

```powershell
dotnet test --project tests/Agentstration.Management.Storage.Tests --configuration Release --no-build --filter FullyQualifiedName~SqliteConnectionPoolTests --minimum-expected-tests 1
```

## Functional coverage

CI collects managed-code coverage from the required Fast and Integration inventory on two partitioned runners while the uninstrumented functional lanes remain in `build-and-test`. The raw shard reports are merged into one consolidated result after both runners complete. Performance workloads, live-provider scenarios, test assemblies, generated sources, and files outside product `src` directories are excluded. Coverage is initially report-only: collection or report-generation failures fail CI, but the measured percentage does not.

After restoring dependencies and building the root solution, reproduce the CI report locally with:

```powershell
dotnet tool restore
./scripts/ci/run-functional-coverage.ps1 -Configuration Release -NoBuild
```

The script merges every module result into `artifacts/coverage/report/Cobertura.xml`, writes the line and branch totals to `artifacts/coverage/report/summary.md`, and generates the browsable report at `artifacts/coverage/report/index.html`. The same summary appears in the GitHub Actions run, and the complete raw and consolidated output is retained as the `functional-code-coverage` artifact.

## Performance lane

Performance workloads live in a dedicated project and are never discovered by the fast or integration lanes:

```powershell
$env:AGENTSTRATION_STORAGE_BENCHMARK_PROVIDER = "Sqlite"
$env:AGENTSTRATION_STORAGE_BENCHMARK_REPORT = "artifacts/storage-benchmark-sqlite.json"
dotnet test --solution Agentstration.Tests.Performance.slnx --configuration Release --no-build --filter TestCategory=Benchmark --minimum-expected-tests 1 --max-parallel-test-modules 1
```

The report records workload parameters, provider, elapsed time, runtime and OS metadata, throughput, median, p95, errors, conflicts, and retries. SQLite remains the local default; set `AGENTSTRATION_TEST_POSTGRES` for an opt-in PostgreSQL run. Pull requests execute only a bounded contention smoke for relevant storage paths. Full workloads run from the scheduled or manual `Storage performance` workflow and publish their JSON reports. Latency and throughput gates remain disabled until representative baselines have been collected; zero storage errors is always required.

## CI concurrency and memory diagnostics

The fast, integration, and performance lanes cap concurrent test modules at 4, 2, and 1 respectively. Host-heavy API and Management modules also use one class worker per assembly. CI reruns the designated hosted modules on two isolated runners (Management API alone, the other modules sequentially) through `scripts/ci/run-test-module-with-diagnostics.ps1` in the dedicated `hosted-module-diagnostics` job. That job runs in parallel with functional coverage and compiles only the designated test projects after restoring the repository graph, so memory safeguards do not extend the `build-and-test` critical path. Each JSON artifact contains the discovered count, duration, process peak working set, process peak private memory, aggregate peak working set for active `dotnet` processes, runtime, and OS. The diagnostic artifact deliberately excludes test output and payloads.

The Management budgets below use Release runs on Windows 11 10.0.26200 with .NET 10.0.10/10.0.11, collected during #298. `Agentstration.Management.Api.Tests` was recalibrated during #745 from 108-test Release runs: 1784.1 MiB peak working set on Ubuntu 24.04.5 with .NET 10.0.12 and 1757.6 MiB on Windows 11 10.0.26200 with .NET 10.0.11. `Agentstration.Management.Security.Tests` was recalibrated from 59-test runs in the same change: 1193.7 MiB on Ubuntu and 1127.6 MiB on Windows. The consolidated `Agentstration.Api.Tests` baseline was collected on the same Windows build with .NET 10.0.11 during #142: 191 tests in 200 seconds, 1503.7 MiB peak working set, 1176.6 MiB peak private memory, and 1621.2 MiB aggregate `dotnet` working set. A warning is evidence to review the Linux and Windows trend; a failure caps regression relative to the retained baseline. Adjust these values only after retaining representative artifacts from both runner families.

| Module | Baseline peak (MiB) | Warning (MiB) | Failure (MiB) | Minimum tests |
| --- | ---: | ---: | ---: | ---: |
| `Agentstration.Api.Tests` | Pending after split | 2048 | 2432 | 23 |
| `Agentstration.Api.Flows.Tests` | Pending after split | 2048 | 2432 | 74 |
| `Agentstration.Api.Execution.Tests` | Pending after split | 2048 | 2432 | 58 |
| `Agentstration.Api.Resources.Tests` | Pending after split | 2048 | 2432 | 80 |
| `Agentstration.Management.Api.Tests` | 1784.1 | 1920 | 2304 | 108 |
| `Agentstration.Management.Bootstrap.Tests` | 355.3 | 500 | 700 | 23 |
| `Agentstration.Management.Security.Tests` | 1193.7 | 1280 | 1536 | 59 |
| `Agentstration.Management.Aep.Tests` | 763.4 | 832 | 1024 | 29 |

The four API modules inherit the consolidated module's current CI warning/failure limits of 2048/2432 MiB. These are retained ceilings, not measured baselines for the new assemblies. In main run `41710631042`, the consolidated module passed all 235 tests but exceeded its failure ceiling at 2464.2 MiB; the preceding run measured 2417.3 MiB. Retain measurements of the split modules before recalibrating their budgets or claiming a memory or duration improvement.

The AEP budget was recalibrated during #916 after the hosted enrollment and bundle reconciliation coverage grew from the original 11-test baseline to 29 tests. All 29 tests passed in both retained runs: [Linux report](/test-diagnostics/aep-linux-2026-11-02.json) (Ubuntu 26.04.1, .NET 10.0.14) peaked at 763.4 MiB, and [Windows report](/test-diagnostics/aep-windows-2026-11-02.json) (Windows 10.0.26300, .NET 10.0.11) peaked at 691.7 MiB. The Linux report retains the original failed status because it exceeded the former 700 MiB cap despite a successful test exit. The Windows baseline was collected without memory thresholds. The revised warning/failure limits are 832/1024 MiB, and the minimum expected count is raised to 29 so missing scenarios cannot silently pass diagnostics. Only the AEP budget changes; the per-assembly worker limit remains one.

## Project classification

| Test project | Lane | Boundary or rationale |
| --- | --- | --- |
| `Agentstration.ArchitectureTests` | Fast | Assembly dependency rules |
| `Agentstration.Console.Client.Tests` | Fast | HTTP and SignalR mappings, pagination, errors, retries, and credential forwarding without an authoritative server |
| `Agentstration.Console.Components.Tests` | Fast | Console Razor behavior, presentation state, permissions, localization, and static assets with mocked clients |
| `Agentstration.ResourceManagement.Tests` | Fast | Resource access policies and descendant-use rules with in-memory ports |
| `Agentstration.Storage.Tests` | Fast | Storage type/version, explicit export mappings, protected resolution, drift, authorization and concurrency with deterministic in-memory ports |
| `Agentstration.Schemas.Tests` | Fast | Schema format selection and capability validation |
| `Agentstration.Secrets.Tests` | Fast | Secret capability authorization, expiry and single-use redemption with fakes |
| `Agentstration.Infrastructure.Tests` | Fast | Internal date/time tools and HTTP fetch validation with a fake handler |
| `Agentstration.Tools.Tests` | Fast | Pure tool validation and in-memory use cases |
| `Agentstration.Tools.SourceRegistry.Tests` | Fast | In-process CLI and manifest validation |
| `Agentstration.Web.Components.Tests` | Fast | bUnit component behavior |
| `Agentstration.Web.FlowDesigner.Tests` | Fast | bUnit and graph projection behavior |
| `Agentstration.Workplace.Components.Tests` | Fast | bUnit component behavior |
| `Agentstration.Application.Tests` | Integration | Application use cases and their SQLite-backed contracts without the executable host |
| `Agentstration.Management.Storage.Tests` | Integration | SQLite control-plane, Identity persistence, secrets, audit, and trigger storage |
| `Agentstration.Management.Sources.Tests` | Integration | Source provider, registry transport, and Pack composition behavior without the executable host |
| `Agentstration.Api.Tests` | Integration | Cross-cutting authentication registration, OpenAPI, MCP catalog, and Tool audit transport behavior |
| `Agentstration.Api.Flows.Tests` | Integration | Flow authoring, persistence, execution, recovery, scope isolation, and hosted Flow APIs |
| `Agentstration.Api.Execution.Tests` | Integration | Hosted Work, Workplace, Runtime Run, SignalR, and AWP worker execution boundaries |
| `Agentstration.Api.Resources.Tests` | Integration | Sources, Source Registry, and Pack integration and transport behavior |
| `Agentstration.Management.Api.Tests` | Integration | Hosted Management API tests using the API-only test profile |
| `Agentstration.Management.Bootstrap.Tests` | Integration | Declarative bootstrap catalog, application, and hosted startup scenarios using the API-only test profile |
| `Agentstration.Management.Security.Tests` | Integration | Identity, authorization, local-account, and interactive Security boundaries |
| `Agentstration.Management.Aep.Tests` | Integration | AEP enrollment lifecycle and extension inventory boundaries using the API-only test profile |
| `Agentstration.ModelProviders.Tests` | Integration, provider-optional | AEP test hosts plus opt-in live-provider checks |
| `Agentstration.Runtime.Tests` | Integration | Runtime business behavior and SQLite reconstruction without the executable host |
| `Agentstration.SourceProviders.Git.Tests` | Integration | Real Git processes and file-system repositories |
| `Agentstration.Web.Tests` | Integration | Combined standalone composition, startup, storage profiles, workers, and lifecycle |
| `Agentstration.Performance.Tests` | Performance, opt-in | SQLite/PostgreSQL relational storage concurrency workloads and machine-readable reports |
| `Agentstration.Workplace.Web.Tests` | Integration | Workplace HTTP host |

The autonomous AEP SDK keeps its own `aep/Aep.slnx` validation because it can be built and released independently from the product solution.

## CI inventory and execution safeguards

Run `./scripts/ci/test-ci-contracts.ps1` to check coverage partition completeness and path-selection regressions. The classifier also runs these checks before selecting jobs. Rename detection is disabled when gathering changed paths so both the removed and added paths contribute to validation selection. Storage/application families, including Resource Planning and Security, and every PostgreSQL project select PostgreSQL checks; Console Web, Client and Components select Browser UX on pull requests and main pushes. The required `build-and-test` check keeps its existing name and documentation-only success path.

Coverage emits one assembly-named TRX per module. Collection and publication check each required module's TRX coverage attachment against its actual Cobertura file; missing, reused, malformed or unexpected reports fail publication; byte-identical TRX attachment copies are consolidated once. Fast and Integration remain authoritative inventories; Coverage A/B must partition Integration exactly. Performance and live-provider execution remain optional.

Linux validation compiles `Agentstration.slnx` once in `build-linux`, plus the autonomous AEP solution when selected. Fast and Integration then run in parallel on separate runners; AEP, both coverage shards, both hosted diagnostic groups and selected PostgreSQL checks also consume that build with `--no-build`. Consumers wait for compilation and artifact publication, not for another test job. Windows lifecycle and container builds remain independent. The required `build-and-test` check aggregates the build and selected functional/AEP results, fails on failed or unexpectedly skipped dependencies, and retains documentation-only success.

The run-scoped Linux artifact contains tracked projects' `bin` and `obj` directories in a Zstandard-compressed tar archive, preserving executable modes, hidden generated files and timestamps. Its context manifest binds it to the checkout revision, exact SDK, OS, architecture, checkout path and NuGet package path; consumers reject mismatches before extraction. GitHub-hosted Linux runners must use matching checkout/package paths because generated static-web-asset files can contain absolute paths. Each consumer still restores and audits packages so a partial NuGet cache cannot silently omit dependencies. The build archive is retained for three days; rerun the producer too if it expires. Archive size and packaging duration appear in the build job summary. Compare artifact transfer costs, total runner minutes and complete elapsed time before claiming a speedup.

AEP tests remain in coverage shard B based on retained collection timings (A/B: 600/321 seconds in run 41668461593). Hosted diagnostics retain isolated Management API and other-module runners, all uninstrumented memory budgets and one worker per assembly. Build reuse changes neither test inventories nor coverage-report completeness checks.

Compare candidate workflow runs with the retained baseline runs 41668461593, 41646766981 and 41498304716 (17m30, 20m and 17m40 elapsed respectively). Retain source SHA, runner/runtime, job and step durations, test totals, coverage, diagnostic memory, complete elapsed time and total runner minutes. These historical runs use different revisions; they establish costs, not a controlled percentage improvement. Candidate measurements are required before claiming a speedup.

Browser UX runs Desktop Chromium in two Playwright shards on separate runners; Mobile Chromium and locale contracts each use one runner. Every runner retains one Playwright worker and isolated product hosts/data. The required `browser-ux` check waits for all four matrix jobs to succeed, and evidence artifacts include the project and shard index. Each runner restores the NuGet package cache using the same key and OS fallback as the main build job; `dotnet restore` and NuGet auditing still run on cache hits. The npm cache remains keyed by the browser workspace's lockfile. Compare complete elapsed time and total runner minutes against Browser UX runs 41733014469 and 41734413136 before claiming a measured speedup; the latter includes a mobile rerun tracked in #997.

## Management fixture boundaries and profiling

The required inventory contains 1,731 cases: Fast contains 777 and Integration 954 (including 12 optional cases without live-provider or PostgreSQL configuration). Twenty-four boundary-free cases moved from Management API/Security into ResourceManagement, Schemas, Secrets and Infrastructure test projects. Their existing test names are retained. Coverage follows these inventories automatically and expects 33 module reports after splitting the API integration assembly.

Flow recovery regressions cover terminal history, multiple active pages, equal update times, exact Instance/Tenant/Workspace keys, active leases, and input expiry while earlier runs become terminal. SQLite query checks require bounded, status-filtered key reads without loading payloads. The PostgreSQL CI lane also runs `FlowRecoveryRepairsLegacyStatusesAndDrainsChangingPages`: it creates a disposable database, upgrades stale status metadata without changing payloads, ETags or update times, and drains pages while visited runs are updated and deleted. Its test connection therefore needs permission to create and drop that disposable database; the default offline lane skips it when `AGENTSTRATION_TEST_POSTGRES` is absent.

Management API retains real HTTP, MCP transport, hosted provisioning and AWP execution tests. Eleven service-only cases instead use `ManagementServiceFixture`: the production application registrations with hosted services disabled, private non-pooled SQLite stores and deterministic bootstrap. Five Storage execution/provisioning cases use a module-owned preparation helper in Management Storage, alongside the existing content/persistence tests. Each test creates its own provider and private non-pooled SQLite databases, without an ASP.NET Core server, background scheduler or Worker, and disposes only its own provider/files. The tests retain real persistence, scope authorization, provisioning and the real artifact backend; all sixteen cases remain in the required Integration and coverage inventories. No host, mutable database or cross-project source compilation is shared.

`SecurityApiTests.Factory` defaults to the existing API-only composition. Tests rendering login/bootstrap/logout, account/PAT/profile, organization or audit pages explicitly request `fullHost: true` and keep their cookie/antiforgery checks. The role/PAT matrix asserts exact statuses and endpoint-specific Problem Details or response properties. Artifact leases are successfully read before expiry, denied exactly at the controlled deadline and denied independently after revocation, without wall-clock sleeps.

Set `AGENTSTRATION_TEST_HOST_METRICS` to a private output directory to collect one JSONL record per completed Management API/Security host. CI includes these files with hosted diagnostics. Records contain startup duration (the factory's `CreateHost` call), active duration (ready until stopping), shutdown-signal duration (stopping until stopped), process peak memory, runtime and OS. The shutdown signal excludes subsequent provider/file disposal; the complete module duration includes test execution and disposal. No requests, credentials or resource contents are retained. Service fixtures do not emit HTTP-host records. Keep the existing one-worker and memory limits when comparing runs; separate host-count reduction from relocation of already host-free tests.

The local comparison for #981 used Debian 13 x64, SDK 10.0.300, test runtime .NET 10.0.8, one class worker and one module at a time. The baseline is `cdb9999` with the same opt-in instrumentation before fixture changes. The candidate preserves all 215 logical test names; 24 deliberately change test assembly when moved to Fast. No concurrent build or functional suite ran during either measurement.

| Module | Tests before → after | HTTP hosts before → after | Module duration before → after | Peak working set before → after |
| --- | ---: | ---: | ---: | ---: |
| Management API | 141 → 127 | 116 → 105 | 380.656 → 358.062 s | 2471.7 → 2329.0 MiB |
| Management Security | 74 → 64 | 37 → 37 | 148.336 → 143.611 s | 1516.2 → 1298.4 MiB |

API host startup summed to 262.246 → 234.186 s; Security startup summed to 62.068 → 59.402 s. These are single-run observations, not a speed guarantee. The diagnostic wrapper's runtime field describes PowerShell; the host records identify the actual test runtime. Full disposal is included in module elapsed time but is not isolated by the host shutdown signal. Both modules remain above their warning thresholds and below unchanged failure limits. Pure-test relocation removes no host starts by itself.

Negative controls deliberately changed an authorized Model Provider operation to return an unexpected 404, and disabled valid Artifact lease recognition. The matrix and pre-expiry read each failed, then passed after restoring the source and rebuilding. Neither mutation is part of the change.
