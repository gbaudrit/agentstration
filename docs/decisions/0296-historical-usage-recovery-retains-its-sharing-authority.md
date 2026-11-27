# ADR-0296: Historical usage recovery retains its sharing authority

- Status: Proposed — implementation for #1348
- Date: 2026-11-27
- Owners: Work Plane
- Related: #1348, #1345, #951, ADR-0275, ADR-0282, ADR-0291, ADR-0293

## Context

An ATC/AFC service reassignment increments the private Project version. The sharing service
previously required that version to equal every retained publication's admission version, including
terminal receipts. After such an unrelated edit, repeating usage publication failed with
`sharing_connection_changed`; publishing a correction also failed while replaying its immutable
ancestors. Observation identities and the actual sharing authority had not changed.

Usage attribution belongs to the original portable Project/participant, plan/task and invocation,
not to today's ConnectedService UID, account, scope or task membership. Existing immutable accounting,
YAML sharing, exact native attempt recovery and authorization boundaries remain authoritative.

## Decision

Allow a retained publication containing only Usage changes to recover or replay after the private
Project version changes. Admission of a new operation still checks the reviewed current version.
The existing owner/Workspace authorization and fresh sharing capability discovery remain mandatory.
The complete sharing connection, including portable Project/participant and logical location, and
the protected publication binding must still equal the retained original. Do not substitute a new
sharing service, target, resource pin or intent, and never rewrite an existing native attempt.

The existing recovery path queries the original effect identity before any resubmission. A terminal
receipt remains the same receipt; an uncertain write is recovered using its original attempt pins.
Immutable record/root identities, correction lineage, counters, measurement quality and invocation
denominators remain unchanged. Corrections reuse retained ancestor publications. Independent local
ATC/AFC service assignments and management metadata are never added to portable usage records.

Other-domain and mixed publications retain the exact Project-version requirement. This narrow
accounting exception does not authorize plan publication, allocation, execution, acceptance or
configuration changes using an older review. No contract version, schema, migration, transport,
store, scheduler or UI journey is added. This extends ADR-0275/ADR-0282 without rewriting them.

## Consequences

- Historical usage and its corrections remain publishable after unrelated local connection edits.
- A lost response, restart and replay retain one effect and one observation across participants.
- Changing the actual sharing connection or publication binding still blocks the operation instead
  of redirecting an uncertain effect. The original local receipt and accounting remain readable.
- Source-native issue authority, local execution and shared functional admission keep their existing
  version and connection checks; accounting cannot weaken them through a mixed batch.
