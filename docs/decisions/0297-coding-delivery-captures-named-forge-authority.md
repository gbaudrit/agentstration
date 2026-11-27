# ADR-0297: Coding delivery captures named Forge authority

- Status: Proposed — implementation for #1349
- Date: 2026-11-27
- Owners: Coding and Work Plane
- Related: #1349, #1346, #1347, #1348, #1378, #1379, ADR-0283, ADR-0291, ADR-0292

## Context

Delivery previously created a PR through the legacy Coding repository operation. Projects now
separate portable Tracking and Forge needs from local ConnectedServices. Independent installations
can use different service/extension UIDs, scopes and accounts, and Tracking and Forge can be supplied
by different providers. A successful process, PR status or matching extension label cannot establish
functional acceptance or authority for another connection.

AFC providers explicitly describe recovery scope and receipt retention. Process-scoped Missing after
an uncertain submit cannot prove that a native PR/MR was never created. The delivery must retain its
original target and effect identity instead of using another account or issuing another create.

## Decision

The existing delivery application service requires explicit logical names for Tracking, Forge,
protected Git publication and sharing. Tracking must match the acknowledged shared issue. Tracking
and Forge must appear in the attempt's immutable Project context, captured through reviewed task
connection selections. Current requirements/assignments must still match that context. Git/sharing purpose bindings retain their names and explicit native adapter selection, and add a
`nativeService` reference to an independently authorized ConnectedService. This is the configuration
selection for the existing native Git capability, not another ATC/AFC contract. The Git extension
contributes a versioned `git` ConnectedServiceType; repository URL, authentication mode, username,
ref/path allowlists and optional fixed sharing ref belong to its options, with the credential supplied
by a secured input. Extension-level repository allowlists remain technical host policy.

Bootstrap `ProjectSharing.nativeServices` resolves portable service bindings and AEP contribution
selectors into private exact references. Preview fingerprints include service/type/configuration and
live AEP route proofs. Attempts, delivery intents and native sharing operations retain those proofs.
Late guards revalidate the original service and route before effect invocation; changing options,
Parameter targets, assignment, scope grants or extension routes cannot redirect a retained write.
Git receives the secured configuration through the existing protected HTTP envelope and governed
Tool pipeline. A configured service cannot be overridden by legacy business arguments. Existing
protected relays remain available to ordinary native consumers; the complete delivery fixture uses
ConnectedServices for all four connection needs.

Keep native Forge wire schemas and status interpretation in an Infrastructure AFC adapter, behind
one narrow Coding port. It reuses the existing connection resolver, governed Tool execution pipeline,
protected service headers and late configuration/access guards. Describe/schema/operation evidence
and exact service/type/target/mapping/route proofs are mandatory. Resolve verifies native repository,
clone URI, base ref and SHA against the retained code repository identity. The existing read-only
Coding repository boundary still supplies checkout identity; Git retains exact commit publication.
There is no new server, SDK account configuration on the extension or alternate scheduler.

Freeze the selected Forge route and recovery guarantees in the private delivery intent. Tracking's
provider/target/item identity remains independent of the code/Forge repository. After code publication,
prepare and persist the exact AFC create digest using the published head SHA, original base SHA,
review content and qualified Tracking link. Always recover that original native operation first.
Before its first Execute, persist a unique submission reservation through the existing optimistic
repository. Only the reservation winner may perform that first submit; a concurrent caller cannot
interpret Missing as permission to submit the same process-scoped operation again.

A process-scoped Missing after a retained submission stays Uncertain. Durable Missing can reuse only
the same frozen key/intent inside the declared window measured from that submission. An expired
Missing cannot authorize another create. Applied receipts validate exact base/head, draft/merge state,
native review identity and Tracking links before recording the effect. The wire digest and submission
reservation are immutable once stored. Changed local assignments or shared review state never redirect
an uncertain create. Existing authorization, three-minute resume deadline, bounded stage steps,
source/test evidence and explicit retry rules remain in force.

Work retains provider-neutral route, reservation and receipt data in its existing private JSON
payloads; shared YAML contributions contain portable native review/Tracking identities and results,
never service/type/extension UIDs, scopes or credentials. Work usage keeps its original accounting
coordinates and recovery from ADR-0296. A native CLI or check failure/cancellation still publishes its
retained evidence/usage without creating code or a review. No PR merge, issue closure, task acceptance,
worker recovery or automatic plan activation is introduced.

The unreleased request and receipt contract is extended in place for fresh/reseeded installations.
No relational schema change, migration or compatibility rebinding is supplied. ADR-0283 remains the
original decision; this record extends its provider boundary and submission recovery policy.

## Consequences

- One portable seed can configure independent local ATC/AFC/Git services and explicit same-contract choices.
- A reviewed YAML realization plan follows the existing draft import, activation and execution path.
- The first source-native GitHub fixture remains executable, while orchestration and its neutral port
  can bind independent Tracking/Forge authorities without vendor schema or status assumptions.
- Losing a provider's process receipts can leave explicit uncertainty requiring recovery of the original
  native identity. A new local service or delivery ID cannot bypass that uncertainty.
- The integrated Console UX remains owned by #942/#943; this API/provider change does not start their
  gated reusable scenario or close the parent acceptance issue.
