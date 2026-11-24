# ADR-0248: Manual Bootstrap plans preserve per-profile targets

## Status

Accepted

## Context

ADR-0246 makes one Bootstrap application durable and independent of its initiating
HTTP request. The original composition accepts one target shared by same-scope
profiles. The Console must now select a target per profile, combine levels, retain
preview/confirmation and observe one operation after reconnect. Earlier profiles
may create the Tenant or Workspace needed by later profiles.

## Decision

Extend the existing Bootstrap selection with an optional bounded `applications`
list. Each item identifies one local profile, its existing target or a named
planned target, and its own bindings. A plan contains at most 16 unique profiles;
legacy flat selections and exact Source selections remain supported. Do not mix
legacy/global target fields, Source provenance and a local plan in one request.

Derive scopes from the authorized server catalog. Execute all Instance profiles,
then Tenant profiles, then Workspace profiles, retaining caller order within each
level. The Console presents this same order and allows reordering only within a
level. A consulted Console scope never supplies an application target.

A planned target is a logical Tenant name and optional Workspace name. When absent
from persisted topology it must be declared by an earlier selected profile. Resolve
existing targets to canonical identities, verify active state and Tenant/Workspace
membership, and reject operational destinations. A binding can identify a producer
profile; validate its position and declared resource identity, then resolve its
physical owner after topology creation. Do not invent IDs during preview or create
resources merely to discover a target.

Preview is read-only. Existing usable targets use the owning resource handlers.
If a target or preceding dependency does not exist yet, mark the affected profile's
semantic validation as deferred. The Console distinguishes that state from completed
validation, explains retention of prior writes, and does not repeat long warnings
for every resource. Structural kind/scope, binding declarations, target provenance
and selection limits remain validated before confirmation.

Pin ordered catalog digests, explicit targets, bindings and available resolution
digests in the confirmed plan digest. Revalidate the whole plan before the first
write and each profile before execution, including authoritative ownership and
resource validation. Previously resolved contributions retain their resolution
digest; deferred dependencies resolve only when their prerequisites exist. Reuse
the existing model-dependency barrier for manual plans without writing their waits
into the initial-startup observation stream.

Keep one durable application, actor, request identity and global sequential-worker
lease for the whole plan. Admission validates its bounded request shape and existing
targets without parsing all manifests; preparation and execution remain in the
background worker. Report aggregate monotonic progress with the original profile
and logical target for each safe resource identity. Cancellation, restart
interruption, retry identity and sanitized terminal history retain ADR-0246's
contracts. Each profile runs under its own explicit request context. Stop on a
failure; already-created resources remain and no rollback is implied.

The Console has separate preparation, preview and application views within the
Bootstrap section. Back retains the current in-memory preparation; edits invalidate
its preview. Confirmation uses the exact preview selection and digest. A preview
is not a durable draft; reloading it requires a fresh preview. Application URLs
retain the durable ID and resume observation without another submission. Catalog
configuration is an independent shared modal, not an intervening workflow step.

## Consequences

Operators can prepare several administrative levels in one reviewed operation.
The aggregate application is Instance-owned and administrator-only; each resource
still has its actual owner and authority. Legacy history without a plan remains
readable. The DTO additions require no relational migration and persist no resource
bodies, parameter values or credentials. Plans with deferred validation may fail
after earlier profiles succeeded; that possibility is explicit before confirmation.

Duplicate occurrences of one profile and mixed local/Source plans remain outside
this contract. The implementation reuses existing handlers, contexts and workers;
it adds neither another execution engine nor a cross-resource transaction.
