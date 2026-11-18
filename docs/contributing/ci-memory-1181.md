# CI memory investigation for PR #1181

## Finding

The CI failure associated with [PR #1181](https://github.com/gbaudrit/agentstration/pull/1181) is a repeatable **per-process working-set budget breach** in `Agentstration.Api.Resources.Tests`. The module completes its test process successfully, then the diagnostic script rejects the measured peak against the retained 2432 MiB ceiling. This evidence does not establish an OS OOM kill, a managed-memory leak, or a functional test regression.

The same failure occurs before and after the merge into `main`. Build, Fast, Integration, AEP, coverage publication, PostgreSQL storage, Windows host lifecycle and container jobs succeeded in both runs. Windows host lifecycle is a different workload; it is not a Windows memory measurement of Resources.

## Retained observations

These values come from the diagnostic job logs, not a new local benchmark. All three runs restored SDK 10.0.404 on Ubuntu 26.04.1. Counts describe each revision's inventory, so this is not a controlled comparison of identical workloads.

| Resources execution | Revision | Tests | Duration (s) | Peak working set (MiB) | Peak private memory (MiB) | Aggregate dotnet peak (MiB) | Diagnostic result |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| [Previous main, run 43312760684](https://github.com/gbaudrit/agentstration/actions/runs/43312760684/job/131515370518) | `cc8e906e1b833e86e365c549ce4c9696376f3407` | 118 | 186.4 | 1314.7 | 1246.5 | 1889.5 | passed |
| [PR, run 44199430615](https://github.com/gbaudrit/agentstration/actions/runs/44199430615/job/134452519253) | head `72374bc1c8e05087f7a2184fbc3d0dcd05b79fe7` | 235 | 365.5 | 3162.1 | 2886.0 | 3770.7 | failed: memory budget |
| [Merged main, run 44248442758](https://github.com/gbaudrit/agentstration/actions/runs/44248442758/job/134615431810) | `8f1645446d7ae9082ed2d00bd28cce98ebaa023a` | 235 | 391.6 | 3181.2 | 2899.3 | 3766.8 | failed: memory budget |

The PR job tests GitHub's merge revision `faebc6f512431a4dc0712c1efd63f02b84e4860d`; its event head is shown above. The merged-main peak exceeds the failure ceiling by 749.2 MiB (30.8%). The 117 additional tests and the new Data Source/Knowledge/Artifact scenarios are investigation leads, not proof of which allocation or fixture caused the increase.

On merged main, the preceding API modules pass: API 42 tests / 1418.1 MiB, Flows 109 / 841.1 MiB and Execution 82 / 1507.5 MiB. [Management API](https://github.com/gbaudrit/agentstration/actions/runs/44248442758/job/134615431791) completes 131 tests at 2251.0 MiB: above its 1920 MiB warning, below its existing 2560 MiB failure ceiling. Bootstrap, Security and AEP diagnostics did not run after Resources failed. Their successful functional tests cannot substitute for missing diagnostic measurements.

## Safeguards and next evidence

- Keep every existing warning/failure ceiling, minimum count and concurrency setting. The documentation table in the testing guide is aligned with the existing workflow; its Management limits were already 2560/1664 MiB before this investigation.
- Record test counts, exit code and all failure reasons independently. A memory breach must remain visible even when tests pass, and a test failure must remain visible even if memory also breaches its ceiling.
- Attempt every selected module sequentially in its own process, upload the reports with `always()`, then fail the diagnostic job if any module failed. This fixes the loss of measurements after the first exception; it does not make Resources meet its budget.
- Retain a new Linux run with all module reports and a representative Windows Resources run at the same revision and Release configuration before proposing recalibration. Keep uninstrumented measurements separate from coverage runs.
- If investigating the cause, compare scenario groups and fixture startup/disposal with the same worker limits, and retain process/managed-heap or allocation evidence before claiming a leak. Any fixture optimization or assembly split needs unchanged-inventory checks and fresh whole-module measurements against the retained ceiling.

The legacy JSON `runtime` field describes the PowerShell collector's runtime, not necessarily the test process runtime. SDK information above is from the setup/build-transfer logs. Do not infer the child's exact runtime patch from the collector field. Source artifacts remain linked from the original runs; this note does not reconstruct unavailable raw reports or retain request/test payloads.

## Correction after the diagnostic run

[Run 44257197896](https://github.com/gbaudrit/agentstration/actions/runs/44257197896/job/134643277641) on #1219 confirmed the same failure: Resources discovered 235 tests, passed 230, failed zero and peaked at 3283.2 MiB. Its only failure reason was `working-set-budget-exceeded`. Collection then continued: Bootstrap measured 651.8 MiB (warning), Security 1203.3 MiB (passed), and AEP 881.8 MiB (warning). Every other CI job passed. This verified the diagnostic correction and left the original process budget breach unresolved.

The follow-up splits the 235 cases into three whole-module processes: Resources (111), DataSources (85), and Artifacts (39). Test methods, data rows and namespaces are preserved; common resource fixtures move to a test-root directory compiled by those three modules. DataSources also retains the six filtered structured-document conformance cases and their PostgreSQL workflow. All three modules are registered in the main solution, Integration and coverage shard A, and inherit the original 2048/2432 MiB ceilings. Their diagnostic floors sum to 230 executed cases (111 + 80 + 39); DataSources also discovers five optional PostgreSQL cases that are skipped in the offline lane. Microsoft Testing Platform excludes skipped cases when enforcing its minimum. The Integration/coverage module concurrency is unchanged.

Verification compares discovered names and data rows before/after, then runs each complete module with its unchanged ceiling. Functional and coverage collection must include all three modules. This changes test process lifetime and does not claim a product allocation fix or a Windows memory baseline.
