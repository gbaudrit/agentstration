# ADR-0206: Knowledge publishes retained Storage content

## Status

Proposed

## Context

FR #1042 extends the Artifact projection defined by ADR-0167/0183 with the native
datasets from ADR-0204. Generic ASP object writes in ADR-0200 deliberately do not
promise immutable versions or retention. Neither a unique object key nor a
capability flag prevents a caller from replacing published bytes.

## Decision

Keep the existing Knowledge Source, editor, dependency composition, projection
service and retrieval Flow. Native bindings pin the Data Source UID, generation
and dataset definition hash. They do not invent a historical Profile. The Data
Source continues to own saved operation parameters and Storage bindings.

A Knowledge Source may select an exact output Storage revision and a narrowing
logical rule. Native inputs require that selection before activation. Existing
disabled native preparations remain editable. Existing Artifact-only definitions
and published Snapshots retain their original execution and content references.
No resource or persisted history is deleted, migrated or silently rebound.
Unchanged dependencies retain their pins during edits. `RebindDependencies` on an
update explicitly adopts current revisions through the same composition resolver;
the editor exposes that choice without changing old Snapshots.

Native content is read through Data Sources' authorized exact acquisition reader.
The bounded initial Flow profile carries inline base64 content alongside the
existing manifest identity, media type and digest. The added `inline` content
kind is not a durable Artifact claim. Transformation and projection Flows may
emit inline content or existing durable Artifacts. Both still execute through
the governed Flow/Runtime/AWP path. Publishing inline content requires an output
Storage. A selected output Storage receives copies of all projected outputs;
input placement never determines the output destination.

Storage owns `IStoragePublicationGateway`: validate support, prepare, seal, and
read an exact retained reference. The first implementation uses the builtin
local Storage. It creates a separate publication namespace beneath the existing
scope/Storage-UID directory, partitioned by execution Workspace. Generic ASP
object operations cannot address these files. Preparation writes and flushes a
bounded temporary document then atomically moves it to a pending identity.
Sealing verifies its complete evidence and digest and moves it without overwrite.
Retries compare complete evidence; different content for the same publication
identity is rejected. No operation rewrites a sealed document.

This is a Storage-owned local publication contract, separate from mutable ASP
objects and independent of the protocol changes in #1085. It changes no immutable
StorageType version or ASP capability declaration. Other providers must implement
actual retained publication semantics before becoming eligible. Read/write-only
ASP providers and OpenDAL are not implicitly eligible.

A Snapshot retains exact output references (Storage revision, consumer/rule,
logical key, version, length and SHA-256, exact implementation and Connected
Service references, manual-mapping and capability digests, and publication contract) and the original Flow and acquisition
evidence. Inline input bytes are omitted from Snapshot provenance. Historical
Artifact entries continue to use their original receipts and backend. Retrieval
resolves the persisted Snapshot, checks its identity and complete content evidence,
reauthorizes Knowledge and Storage use, and reads only the recorded publication.
The built-in retrieval Tools reject supplied evidence differing from that record.
Storage configuration edits fail closed as stale; they never retarget an old
reference. This increment does not resolve an obsolete revision against a newer
binding. The retained files remain intact even when access becomes unavailable.

Projection acceptance persists a request identity before resolving inputs. Exact
inputs are persisted before running transformations. Flow and publication identities
are deterministic within that attempt. A retry of a completed request returns the
same projection; conflicting reuse is refused. Admission allows at most 32 executing or queued projection calls and 1,024
tracked source gates per process. Each source uses an in-process gate; conflicting cross-process
acceptance is rejected by the resource store. Late completion cannot displace a
newer accepted projection during activation. Overlapping acquisition requests for
the same dataset retain Data Sources' explicit already-running refusal; no stale
fallback is introduced. A pending projection can resume
after restart using its recorded caller, logical context, parameters and inputs.
The existing recovery worker invokes the same Knowledge service under that caller's
authorization. Revoked or changed dependencies end recovery explicitly. Caller
cancellation stops waiting; accepted Flow/acquisition work remains resumable and
is not silently cancelled for other consumers.

The complete manifest is validated and prepared before sealing any new output.
An interruption after sealing or during Snapshot creation remains pending, so
retry/recovery uses the original Flow and publication identities. A fault-injection
test interrupts the real local publisher after sealing, then exercises retry and
actual host replacement with an interrupted activation.

Preparation/sealing, Snapshot creation, activation and projection completion remain
separate durable operations. No distributed transaction is claimed. Failure before
activation preserves the previous active Snapshot. A retry of an already-created
Snapshot completes its activation instead of skipping that step.

Freshness is per binding. Empty and zero maximum age are equivalent: refresh off
reuses any-age compatible success; refresh on requests an acquisition for each
projection attempt. With positive age, refresh off rejects stale or missing input;
refresh on reuses a recent compatible success or acquires first. Age is measured
from successful completion using `TimeProvider`, inclusive at the boundary.
Selection orders compatible successful acquisitions by completion descending and
name ascending. Native identity includes saved dataset/operation/Storage parameters;
historical identity includes source generation, target and Profile version/hashes.
An explicit acquisition is checked against those same criteria and is never
silently replaced. Optional failures become provenance issues; no ready inputs is
an error. A failed refresh is never replaced by an out-of-policy stale result.

## Consequences

- The initial local publication profile is bounded to 64 KiB per content object
  and 1,024 retained documents per Storage and execution Workspace. Lower envelope
  bounds continue to apply. Publication capacity exhaustion is an explicit error.
- Unsealed leftovers older than one day are cleaned in bounded batches; sealed
  publications have no deletion API or automatic garbage collection. Retention
  requires operator-managed persistent content and control-plane storage. The
  contract does not promise protection against host filesystem loss or an operator
  deleting files. It does not claim a distributed transaction or backup service.
- Storage usage reporting retains Knowledge definitions and publications so normal
  deletion cannot remove an actively referenced Storage.
- The Console reuses its editor, projection, Snapshot history and retrieval tabs;
  it adds output Storage, prefix and refresh controls with en-US/fr-FR text.
- #1033 remains the deferred reusable integrated browser journey, blocked by
  explicit implemented-UX validation and required implementation merges. This
  change supplies implementation tests and smoke checks, not that reusable journey.
