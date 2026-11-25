# Work token usage

Work usage captures terminal, owned WorkPlan attempts into the Work store before publishing them
through [Project sharing](project-sharing.md). It consumes canonical Flow totals or accepted native
Coding receipts; it never reads CLI account files, prompts, patches or logs into accounting.
The default and deterministic test paths remain offline.

## Capture and provenance

Routes are under `/api/workspaces/{workspace}/projects/{projectId}/usage` and require the existing
Workspace access plus run-read or run-execute authorization. The Project is private to its owner.

| Method and suffix | Behavior |
| --- | --- |
| `POST /captures` | Body `{ "planId": "<uuid>", "attemptId": "<uuid>" }`; captures a terminal historical attempt. Returns durable entries and `replayed`. |
| `GET /?skip=0&take=25` | Immutable history, at most 100 entries per page, with explicit `hasMore`. |
| `GET /{usageId}` | Reads one exact owned entry, including its source and correction provenance. |
| `POST /summary` | Body described below; reads complete local accounting or the last explicitly pulled shared state. |
| `POST /{rootId}/corrections` | Appends an explicit correction using the expected previous ID/digest. |
| `POST /{usageId}/publication` | Publishes the immutable record and any unpublished ancestors; returns the ordinary `202` sharing operation receipt. |

Capture requires the Project context retained at admission, the exact plan revision/digest and a
terminal functional attempt. An explicit retry is another attempt and another original record;
failed/cancelled attempts are preserved. Old contexts without a captured sharing identity remain
local-only. Changing a current connection never adopts or reassigns historical usage. Replay reads
the retained originals. Missing/unfinalized, expired, conflicting or oversized execution evidence
returns a technical error and creates no partial capture batch.

Portable accounting includes a format version, stable record/root IDs and revision, portable shared
Project and producer participant (when captured), plan/task revisions and digests, captured task
ancestors, functional attempt, root Flow Run and optional AWP/native invocation coordinates. Native
streaming reports, final receipt and Flow output are copies of the same identified observations,
not separate charges. Turn observations within one native invocation sum counters but retain one
invocation denominator. A retained native coordinate identifies a dispatched invocation, not a
claim about a provider's internal model-call count. Unreported AWP attempts with no native receipt
have unknown cardinality, exposed separately from a proven never-started assignment.

## Counters and rollups

Input/output/provider total are independently nullable. A measured zero is zero; unknown is `null`.
The provider total is never calculated from other fields. Coverage is `Complete` only when every
included invocation reports all three, `Partial` when some canonical data exists, and `Unavailable`
when none does. Aggregate fields can all be non-null while coverage is partial, because different
invocations may report different subsets. Cached input and reasoning output are optional subsets;
they are never added to input, output or total. Original observation nulls remain available.

A summary request is, for example:

```json
{
  "planIds": [],
  "taskId": null,
  "includeDescendantTasks": false,
  "flowBoundary": "Inclusive",
  "source": "Local",
  "participantId": null
}
```

`participantId` optionally selects a portable producer/instance identity; omitted it includes all authorized producers. An empty plan list selects the Project's retained accounting. An explicit list selects only those
plans; duplicates are deduplicated, with at most 100 requested IDs. Dependencies and cross-plan
waiting never expand that list. `taskId` selects that task; `includeDescendantTasks` also selects
records whose captured ancestors contain it. `flowBoundary` chooses `Direct` or `Inclusive` for
each functional root; the two are never added together. Child Flow runs are already represented
by canonical inclusive totals and are never captured as independent functional roots.

Results expose `canonical` measured counters with coverage across all selected invocation denominators, and separate `measured`, `estimated` and `unknown` totals and expose original count, correction
count, unknown invocation cardinalities, legacy record count and selected plan IDs. Each bucket's
`direct` and `inclusive` fields both contain the chosen Flow boundary; these are alternatives, not
additive totals. Estimates contribute no canonical counters or reported invocations; unknown cardinalities prevent complete canonical coverage. Empty buckets have null counters and zero known invocations. Summaries validate
full correction chains and reject forks, duplicated execution roots, incomplete histories or more
than 5,000 records. They never report a page prefix as a complete total.

## Corrections and shared recovery

A correction body contains `commandId`, `expectedPreviousId`, `expectedPreviousDigest`, `measurement`
(`Measured`, `Estimated` or `Unknown`), replacement `totals`, a bounded machine-readable `reason`,
and an opaque bounded `evidenceReference` such as `receipt:provider-123`. Credentials, query strings
and arbitrary prose are not evidence references. Counters/quality may change; execution identities,
source observations, attribution and invocation counts cannot. A correction receives a new record
ID and references its predecessor. The original remains readable. Concurrent corrections against
the same predecessor have one winner; replaying an identical command returns its retained result.

Publication reuses protected Git compare-and-swap and its durable operation journal. An uncertain
response is recovered through the returned sharing operation or by repeating the usage publication
route. Neither path duplicates native effects. Each correction's ancestors are published first.
The current portable Project/participant must match admission provenance. No automatic Bootstrap,
plan activation, outcome acceptance, issue closure or pricing operation accompanies usage exchange.

Pull shared state through the ordinary sharing endpoint, then request `source: "Shared"`. A second
instance needs its own authorized private Project/connection but no copy of the producing database
and no imported plan merely to read accounting. New Usage content has typed `accounting` and empty
legacy `counters`. Counter-only legacy records remain readable and increment `legacyRecords`; a
named `tokens` counter does not establish input/output/provider total coverage. The original sharing
limits (64 retained records, 16 KiB content and bounded publication envelopes) still apply.
