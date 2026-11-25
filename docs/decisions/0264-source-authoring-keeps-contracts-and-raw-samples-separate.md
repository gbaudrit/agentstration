# ADR-0264: Source authoring keeps contracts and raw samples separate

## Status

Proposed

## Context

[#1373](https://github.com/gbaudrit/agentstration/issues/1373) needs shared guided
editing for Data Sources, named Knowledge bindings and post-aggregation sequences.
A JSON transport contract does not describe native XML inside `content`; a final
published result does not necessarily retain the original input. Production
publication and progress remain owned by [#1369](https://github.com/gbaudrit/agentstration/issues/1369).

## Decision

Operation definitions retain provider-discovered contracts separately from explicit
administrative native-content adoption. Adoption requires a reviewed ETag, write
permission and an exact published Schema resolved through existing format/scope/use
and snapshot mechanisms. XSD validates only the envelope `/content` boundary;
transport validation remains independent. Rediscovery retains adoption. A bounded
history of 64 content revisions retains exact previous service/type/content pins;
further adoption fails instead of silently evicting evidence. Current permission,
availability and exact producer checks still apply. New Schema versions never
implicitly rebind consumers or accepted executions.

Authoring, admission and replay reuse exact Function/Flow resolution, Schema
snapshots and transformation engines. Collection suggestions come only from declared
JSON Schema, using bounded local-reference resolution. Data pointers and generated
Schema pointers remain distinct. Item evidence retains parent identity/version/hash
and selected-definition evidence. Changed upstream evidence requires deliberate
correction. A sample never becomes an authoritative Schema. Compatibility is verified,
incompatible or undetermined; MIME/type equality alone is insufficient.

Raw sampling is a separate explicit authoring command: the exact configured read-only
operation executes once, before Data Source transformations. Transport and adopted
native content are validated separately. Invalid samples remain inspectable but cannot
be replayed as conformant input. Admission returns `202 Accepted`; a bounded queue of
16 commands and one hosted processor reuse current authorization and a two-minute
deadline. Queued commands retain authorization restrictions. Observation starts no work;
cancellation and deletion are explicit.

Samples reuse the existing process-local execution payload store and its 256 MiB
global, 64 MiB Workspace and 4096-entry admission budgets. A raw sample is at most
1 MiB, belongs to its original principal/Tenant/Workspace/logical context and expires
after 15 minutes. Expiry immediately denies reads; physical entries are purged on
mutation and by the sample worker at most one minute after expiry. Explicit deletion
removes content. Restart loses samples. No published result is fabricated into a raw
sample and no payload is copied into ordinary telemetry or production history.

Replay retains intermediate values through the same compiled JSON/XML engines and
exact runtime checks. It never calls the operation again. Two concurrent replays,
a ten-second deadline and an 8 MiB total intermediate-value budget bound this pure
execution path; exceeding a bound reports failure rather than truncating values. Arbitrary Flows and Artifact
functions without an enforceable preview boundary are explicitly unavailable and stop
dependent preview. Draft/sample fingerprints identify stale results. Left-card selection
alone controls the right value; contract disclosure stays on the left and has no
execution/save/ETag side effect.

Knowledge binding inputs are exact compatible authorized completed retained Data Source
results, already transformed upstream. They use the same inline Artifact manifest as
real binding consumption, never the raw Connected Service XML. Standard aggregation
prepares each named binding once, preserves names/contracts/required policies and
optional failure evidence, then prepares one builtin projection result. Compatible
custom points directly consume the governed acquisition/projection boundary; other
fixed/custom execution remains explicitly unavailable. No preview writes production
Storage, registers a production Artifact, changes freshness or publishes/activates a
Knowledge Snapshot.

The generic XML/mapping/function-exposure and viewer slice is reused from
[PR #1218](https://github.com/gbaudrit/agentstration/pull/1218), initial baseline
`5e4a5ed67e2856d0dffc7ea57ca091e22acb36c6`. That PR subsequently merged into main
at `e052dadfe511b97985a040fefb76a4b37d544797`; integration reconciles against its
merged implementation rather than maintaining another copy of the engines. RSS profiles, AEP discovery sharing,
history finalization and the complete branch are excluded. Reconcile shared composition,
function contracts, operation executor and editors against #1218/#1369 before integration,
then run the combined regression suite.

## Consequences

- Additive JSON resource fields require no relational migration or historical backfill.
  Published Schemas/Flows and accepted evidence remain immutable.
- Direct inputs can be prepared explicitly from an authorized raw Data Source sample
  through its saved sequence once, without a production acquisition. Historical Profile
  inputs without an exact native consumption boundary remain unavailable; rendering never
  starts production work to hide unavailable input.
- Recursive JSON inspection supports scalar/object/array roots and progressive expansion:
  at most 512 visible rows, 100 children per container, 12 levels, 512 displayed characters
  per scalar and a 65,536-character source excerpt. These limits do not truncate execution.
- Reusable UX journey [#1374](https://github.com/gbaudrit/agentstration/issues/1374) remains
  blocked until explicit maintainer UX validation and implementation merge. Behavior tests,
  existing regressions and implementation smoke remain required. The parent stays open.
