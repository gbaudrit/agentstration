# Bootstrap test memory investigation for PR #1224

## Finding

The only failed job in [run 44839591447](https://github.com/gbaudrit/agentstration/actions/runs/44839591447)
was the hosted-module diagnostic. Bootstrap completed all 60 tests successfully,
but its 845.3 MiB peak working set exceeded the existing 700 MiB process ceiling.
Functional Fast/Integration, coverage, Browser UX, Resource Plan UX, container,
PostgreSQL, Windows lifecycle and the other workflows passed. This is a memory
budget breach, not a functional failure or evidence of an OS OOM kill.

The prerequisite branch at `54bc22f0` passed its diagnostic with 53 tests and a
622.0 MiB peak. PR #1224 adds seven durable-application cases, including execution
and reapplication of the committed 512-resource profile. The larger workload was
being retained in the same test process. These measurements do not identify a
product memory leak.

## Process boundary

Keep framework contracts in `Agentstration.Management.Bootstrap.Tests`. Move the
durable-application cases and the versioned Assistant/documentation profile
scenarios into `Agentstration.Management.Bootstrap.Scenarios.Tests`. The two
modules discover 50 and 10 cases respectively.

- Preserve test method/class names, data rows, assertions, the complete
  512-resource fixture and the disconnect check beyond the ordinary 15-second
  request budget.
- Compile the same API-only WebApplicationFactory setup in both modules. Existing
  internal-test access is extended to the new assembly; no runtime implementation
  changes are required.
- Register both modules in the main solution, Integration, coverage shard A and
  the hosted diagnostics inventory.
- Keep the original 500/700 MiB warning/failure limits and the original module's
  minimum of 31. The scenario module uses the same limits and requires all 10
  cases. Retain one class worker per module and the existing global concurrency.

This bounds test process lifetime, following the resource-family split recorded
in [the earlier investigation](ci-memory-1181.md). It does not recalibrate a
memory budget or claim a product allocation fix.

## Verification

The local comparison used Release binaries, .NET 10.0.8 and Debian 13. Each test
process ran separately with one class worker. RSS was sampled every 100 ms,
matching the interval used by the CI collector. Local samples read the Linux
process's `VmRSS`; they do not replace a new measurement by the CI collector.

| Execution | Cases | Passed | Failed | Peak working set (MiB) | Ceiling (MiB) |
| --- | ---: | ---: | ---: | ---: | ---: |
| Failing CI module at `df6fa392` | 60 | 60 | 0 | 845.3 | 700 |
| Local reproduction before split | 60 | 60 | 0 | 864.2 | 700 |
| Complete framework module after split | 50 | 50 | 0 | 660.2 | 700 |
| Complete scenario module after split | 10 | 10 | 0 | 658.6 | 700 |

Both new process measurements remain above the 500 MiB warning, which is retained
and remains visible. They are below the unchanged failure ceiling.

Discovery before and after the move contains the same 60 names and data-row
labels, with no omissions or additions. The SHA-256 of the sorted discovery
inventory is `823486dc986c304ab37958fa4fb4cef03560ac8dbe41eaada48702daa70ea6e0`.
Integration equals the union of coverage shards A/B, with no duplicate projects
and no overlap with Fast. The subsequent CI run must still enforce the original
diagnostic rules for both complete modules.

## Multi-target plan follow-up in PR #1260

[Run 44955436632](https://github.com/gbaudrit/agentstration/actions/runs/44955436632)
passed compilation and both functional lanes, but the scenario module now contained
16 cases after the addition of six ordered multi-target plan tests. All 16 passed;
its 758.4 MiB peak exceeded the unchanged 700 MiB failure ceiling. A local Release
reproduction on .NET 10.0.8 also passed all 16, with an 815.6 MiB peak measured from
Linux VmRSS at 100 ms intervals. Repeated isolated test hosts accumulated memory
over the process lifetime; these measurements do not establish a product leak.

Move the six plan cases, without changing their names or assertions, into
`Agentstration.Management.Bootstrap.Plans.Tests`. The existing scenario module
returns to its original ten cases. Share only the API-only host fixture and the
existing application test helpers from the test root; production behavior is
unchanged. All three Bootstrap modules retain one class worker, their own process,
and the same 500/700 MiB warning/failure limits. The plan module requires all six
cases. Register it in the main solution, Integration, coverage shard A and hosted
diagnostics. The coverage shards must still equal Integration exactly.

The Release solution build and coverage inventory check passed locally. The Cloud
execution environment became unavailable during subsequent diagnostics and browser
setup, so post-split memory and complete-suite results must be verified by the new
CI run before claiming that the failure is resolved.
