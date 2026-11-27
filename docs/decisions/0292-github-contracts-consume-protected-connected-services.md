# ADR-0292: GitHub contracts consume protected Connected Services

## Status

Proposed — implementation for [#958](https://github.com/gbaudrit/agentstration/issues/958).

## Context

ADR-0273 supplies native GitHub operations and write-ahead journals. ADR-0289 and
ADR-0291 establish typed ATC/AFC negotiation and named Project service assignments.
Canonical contract Tool schemas cannot contain vendor credentials or server options.
The delivered service resolver and AEP MCP transport already support protected values
and per-invocation HTTP headers; a new service lifecycle or provisioning engine is unnecessary.

## Decision

The GitHub extension contributes versioned `github` ConnectedServiceType options
(API base, exact repository name, managed label prefix), standard target/binding/revision
and native subject inputs, plus a required secured credential input. AEP enrollment
materializes this type through existing governance. Registration configuration remains
extension transport authentication and operational egress/state-volume settings. GitHub
account/server/repository configuration belongs to each service.

Expose independent canonical ATC and AFC contributions alongside the native Tools still
consumed by existing Coding services. SDK server/client adapters own wire validation.
Vendor HTTP DTOs and GitHub semantics remain in the extension. Native issue intake and
Coding delivery adoption remain #1346/#1349, rather than changing those policies here.

Each typed contribution declares the bounded consumer metadata profile
`agentstration.work.connected-service.http.v1`: version `1.0`, service type ID, exact
type revision, schema digest and normalized input/export declarations. The protected
input declarations must match; equal option schemas cannot downgrade a secured credential
to a literal standard input. Product resolution requires local Tool metadata to
match the authenticated live contribution, including presence/absence of the profile.
It checks the selected service proof before resolving values. The execution-only
`X-Agentstration-Connected-Service` header carries type/revision/schema, options and
resolved input strings as bounded base64 JSON. This is an explicit Work consumer adapter
profile, not a new ATC/AFC schema or an AEP ConnectedService lifecycle.

Reuse the existing protected service resolver and `ResolvedSecretInputs` lifetime and
response protection. A transient, nonserialized Tool context callback resolves headers
only during invocation, after route verification; inventory and metadata inspection do
not resolve credentials. Header configuration is at most 18 KiB before base64. Neither
headers nor values enter Work state or Tool arguments; raw credentials and encoded
configuration are redacted from returned output. Generic MCP failures on this protected
path use bounded diagnostics. Existing late service, route and native-schema guards
remain active before effects. HTTP header trace logging must retain default redaction.

Each call authenticates its native user through GitHub `/user`, checks its configured
subject ID, API authority and exact repository native ID before Describe, reads, effects
or cached receipts. The initial account mechanism supports user/PAT credentials;
installation-token-specific identity discovery is not claimed. The provider-qualified
authority hashes the normalized API base, and the target is the native repository ID.
Opaque local binding/revision inputs remain independent from portable external identity.

Derive journal keys from the original operation key, authority/target/binding/revision,
verified native subject and nonsecret options. Persist existing write-ahead journals,
never credentials. Account or target changes cannot recover another connection's
receipt. Keep recovery scope **Process**, despite retained on-disk receipts: a missing
or lost volume cannot prove the absence of an external effect and cannot authorize
blind replay. Existing receipts recover after restart; failed/ambiguous observations
remain uncertain. Observation concurrency explicitly does not promise atomic GitHub CAS.

Tracking supports list/read/create, additive managed classifications and native parent/
blocked-by addition. Other issue edits, comments and transitions are explicitly unsupported.
Only same-target relations are projected; foreign or incomplete authority fails closed.
Forge supports resolve/read/create. Reviews/check observations remain unsupported and
are advertised accordingly. Validate native item numbers and ref paths before HTTP.
A bounded typed PR footer preserves nullable logical body and explicit tracking references
alongside the native operation marker; typed reads and receipts reconstruct that content.

## Consequences

Two services of one type may use different accounts or repository restrictions without
retargeting active work. The default application remains offline/provider-optional.
No relational migration, new UX journey, vendor SDK requirement or protocol foundation
change is introduced. Offline tests cover actual AEP/MCP schemas, governed type
materialization, protected service resolution, SQLite host restart, response loss,
subject/target rejection, unrelated-label preservation and response redaction. Live
GitHub permissions and behavior require a separately opted-in disposable-repository smoke.
