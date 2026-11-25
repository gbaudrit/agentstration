# CI remediation for PR #1273

## Failures

[Run 45040215677](https://github.com/gbaudrit/agentstration/actions/runs/45040215677)
on head `de066f4a` has two independent causes. Browser UX, compilation, formatting,
Fast, Resource Plan UX and documentation passed.

- Bootstrap's `AcceptedLoadTestSurvivesClientDisconnectAndReportsMonotonicDurableProgress`
  expects an exception when reading a Parameter in a nonexistent Tenant. The
  exact-read behavior introduced by this PR returns `null` for a nonexistent resource scope
  (`SqliteResourceStore.GetExactAsync`), before evaluating an existing scope's
  read permissions. The Bootstrap test added on main retained the earlier exception expectation.
  This integration mismatch fails Integration, coverage shard A and the scenario diagnostic. Change that assertion to require absence. Keep the
  positive read from the actual Tenant, all 512 resources, durable progress,
  disconnect, replay and authorization assertions.
- Management API completes all 164 cases, but its process peaks at 2716.5 MiB
  against the retained 2560 MiB ceiling. The preceding source run also passed
  all 164 cases and breached the same ceiling at 2776.9 MiB. This is a repeated
  working-set budget breach, not evidence of a product memory leak or an OS kill.

The failed run tests merge revision `886c831c3b3920f88b787b5c036a381a796a62f1`.
The diagnostic uses SDK 10.0.404; do not infer the child runtime patch from the
PowerShell collector's runtime field.

## Bounded test processes

Following the existing [resource-family](ci-memory-1181.md) and
[Bootstrap](ci-memory-1224.md) process boundaries, move the five Model/Profile/
Provider test classes into `Agentstration.Management.Models.Api.Tests`. All 40
model cases retain their method names, data rows and assertions. The remaining
Management API module retains 124 cases. Their combined inventory remains 164.

Both modules compile the same test-root host/Worker fixture sources, with private
per-test state. They keep one class worker per process, the unchanged 1920/2560
MiB warning/failure limits and the Integration/coverage concurrency limits. The
diagnostic runs both modules sequentially on its existing Management API runner.
The minimum counts now require all 124 + 40 cases. Register the new module in the
root solution, Integration, coverage shard A and hosted diagnostics.

No application logic, UI or memory budget changes. The Worker grants its existing
internal test access to the additional test assembly. Host metrics use the
containing assembly name so the two modules' records remain distinguishable.
This bounds the workload retained in each process; it does not establish an
allocation fix or recalibrate a Windows baseline.

## Verification

Validation results are recorded after whole-module runs below. Subsequent GitHub
checks remain authoritative for the hosted runner and complete functional lanes.
The visual evidence on product revision `1e52fb3e` remains applicable: this
remediation changes test organization, a test expectation and test inventories;
the rendered product sources are unchanged.

The corrected source is `59bcdfe583e3a73c7863a5e317ba3e14a75d5ec1`. [Retained local report](/test-diagnostics/ci-1273-local.json) includes inventory and source-byte proofs. The sorted discovery SHA-256 is `5f9aaa7723a4adda2c777c4e9b1a3e91b6c8b7fe1e94e1ef88df94396212fce6`.

| Uninstrumented local execution | Cases | Passed | Failed | Peak working set (MiB) | Failure ceiling (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Before: consolidated Management API | 164 | 164 | 0 | 2667.3 | 2560 |
| After: Management API | 124 | 124 | 0 | 2125.2 | 2560 |
| After: Model API | 40 | 40 | 0 | 945.6 | 2560 |
| Before: Bootstrap scenarios | 10 | 9 | 1 | 577.3 | 700 |
| After: Bootstrap scenarios | 10 | 10 | 0 | 572.6 | 700 |

Local runs use Release, SDK 10.0.300 and test runtime .NET 10.0.8 on Debian 13. Before runs sample Linux VmRSS every 100ms; after runs use the repository diagnostic scripts. The raw report runtime field belongs to the PowerShell collector, not the child test host. Concurrent collectors/Bootstrap/Fast tests and build servers affect aggregate dotnet memory; the figures above are per-process peaks. Management API and Bootstrap retain visible warnings. No Windows baseline or product allocation improvement is claimed.

The initial Fast check identified the missing explicit architecture allowlist for the new test-root fixture owners. Only the two fixture paths and the new module’s existing metrics link were added to that list; unrelated cross-project source compilation remains rejected. The focused architecture check passes. The Release solution build, formatting verification, diagnostic-classification regression and exact Integration/coverage inventory checks pass. Full Fast/Integration and consolidated coverage results are reported on the PR; final hosted CI is still required.

A subsequent uninstrumented Fast execution overlapped coverage instrumentation and produced Bad IL errors. That execution is discarded as invalid: coverage mutates the same output assemblies while they are loaded. Complete functional validation and instrumentation must be serialized when they use the same output tree. The completed uninstrumented module diagnostics above predate that overlap and are unaffected.

The serialized coverage campaign completed Fast with **3146 passed, zero failed/skipped** before starting Integration. Integration and consolidated report generation were still running when this evidence was committed; use the PR validation status for their completed result.

## After synchronizing main on PR head 2d7f0b59

The [new CI run](https://github.com/gbaudrit/agentstration/actions/runs/45656333424) passes functional Fast/Integration and consolidated coverage, but Bootstrap framework diagnostics now run **61 cases** and reach **738.9 MiB**, exceeding the unchanged 700 MiB failure ceiling. Local reproduction passes all 61 cases at **711.2 MiB** and fails the same budget.

Corrective source: `61328e0794d0e226bb76c2287f490c32098fdb86`. Move the initialization coordinator, AEP bundle installer and complete-profile application cases into `Agentstration.Management.Bootstrap.Startup.Tests`. The original framework module retains **34** cases; the startup/profile module runs **27**. Their exact sorted discovery inventory, including data rows, still contains the same 61 cases (SHA-256 `0215e18e08185b4d9b37bf81b2c85ebaee98cf0d242829e8896bfae8dc6a15f2`). All three moved files are byte-identical. Extract the existing private declarative fixture without changing its helper or test-method content (LF-normalized Git/checkout proof recorded). Share only the two explicit test-root fixture paths through the architecture allowlist, and grant the new assembly the existing initialization internals access.

| Uninstrumented local execution | Cases passed | Peak working set (MiB) | Failure ceiling (MiB) |
| --- | ---: | ---: | ---: |
| Before: consolidated framework | 61 | 711.2 | 700 |
| Rejected intermediate: framework | 45 | 701.2 | 700 |
| After: framework | 34 | 567.1 | 700 |
| After: startup and complete profiles | 27 | 488.0 | 700 |
| Unchanged scenarios | 13 | 657.6 | 700 |
| Unchanged plans | 6 | 429.5 | 700 |

Moving only the 16 lightweight cases was insufficient; retain that rejected measurement explicitly. The final split bounds complete-profile host retention as well. Preserve one class worker per process, 500/700 MiB budgets, existing lane concurrency and sequential diagnostics. Register the new process in the root solution, Integration, coverage shard A and hosted diagnostics. Strengthen diagnostics to require **34 + 27 + 13 + 6** Bootstrap cases.

[Retained measurements and source/inventory proofs](/test-diagnostics/ci-1273-resync.json) use Release, SDK 10.0.404, child runtime **10.0.15** and Debian 13. The collector runtime field belongs to PowerShell, not the child. Concurrent browser hosts and format verification affect aggregate dotnet memory; compare the per-process peaks above. Warning budgets remain visible. No product allocation fix or Windows baseline is claimed.

Release restore/build and changed-source formatting pass. Existing schema-contract browser regression maintenance also passes nine repeated checks; [native screenshots and reproduction](../ux/evidence/1257/ci-schema-contract-bindings/README.md) document the independent shell-readiness race. Full local functional coverage and final hosted CI were pending when this evidence was committed; PR #1273 Validation records their final results. Earlier results in this page remain historical to their stated revisions.

## Main e052dadf and the next hosted memory breaches

The [subsequent hosted run on a93b6fd7](https://github.com/gbaudrit/agentstration/actions/runs/45665894414) passes all 13 Bootstrap scenario cases at **741.0 MiB** (ceiling 700), and all 49 AEP cases at **1030.9 MiB** (ceiling 1024). Functional success does not waive either failure. Keep the already separated framework/startup workloads and additionally separate durable progress from scenarios, and automatic AEP enrollment from the core AEP process.

After integrating `main` at `e052dadf`, the Data Sources process includes three new RSS transformation publication cases and passes 85 cases with five optional provider skips at **2407.6 MiB**, only 24.4 MiB below its unchanged 2432 MiB ceiling. Separate the three RSS rows into `Agentstration.Api.DataSources.Rss.Tests`, preserving the remaining 87 cases and the existing 2048/2432 MiB budgets on both modules. This removes a measured lack of margin without waiting for another hosted breach.

Candidate product/test code: `171b792a74902bf2ad78bba01e62c06000aafc5e`. The new process boundaries preserve exact sorted discovery, including data rows: **13 = 6 scenarios + 7 progress**, **49 = 38 core AEP + 11 automatic enrollment**, **90 = 87 core Data Sources + 3 RSS**. Moved source Git blobs and LF-normalized method/helper extraction proofs are retained in [the report](/test-diagnostics/ci-1273-main-e052.json). The extracted shared fixtures are explicitly allowlisted by path; unrelated cross-project source compilation stays prohibited.

| Uninstrumented local module | Passed | Optional skipped | Peak working set (MiB) | Failure ceiling (MiB) |
| --- | ---: | ---: | ---: | ---: |
| Agentstration.Management.Bootstrap.Tests | 34 | 0 | 569.5 | 700 |
| Agentstration.Management.Bootstrap.Startup.Tests | 27 | 0 | 491.8 | 700 |
| Agentstration.Management.Bootstrap.Scenarios.Tests | 6 | 0 | 458.4 | 700 |
| Agentstration.Management.Bootstrap.Progress.Tests | 7 | 0 | 517.1 | 700 |
| Agentstration.Management.Aep.Tests | 38 | 0 | 874.6 | 1024 |
| Agentstration.Management.Aep.Automatic.Tests | 11 | 0 | 398.3 | 1024 |
| Agentstration.Api.DataSources.Rss.Tests | 3 | 0 | 556.8 | 2432 |
| Agentstration.Api.DataSources.Tests | 82 | 5 | 2078.8 | 2432 |

Root solution, Integration, coverage shard A (Progress/RSS), coverage shard B (automatic AEP), internals access and the existing sequential hosted diagnostics include the new processes. Keep one class worker, every original assertion, unchanged budgets and lane concurrency. Warnings remain visible. These are process workload bounds, not a product allocation fix or a Windows baseline.

Release solution build and changed-source formatting pass. The diagnostic-classification regression passes. All 14 final browser checks pass on the frozen candidate, including eight grid variants. The newly reproduced Data Source stale-load exception has its own deterministic product regression and [before/after evidence](../ux/evidence/1257/data-source-loading/README.md). New `main-e052dadf` grid screenshots and observations replace the prior validation as current evidence; the earlier statements about unchanged rendering apply only to the historical remediation sections above.

Full serialized functional coverage is running when this evidence is committed; PR #1273 Validation records the completed result and final hosted checks. Browser hosts and uninstrumented diagnostics finished before coverage instrumentation started.
