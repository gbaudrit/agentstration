# ADR-0266: Instance extension options use a separate operational AEP contract

## Status

Accepted

## Context

FR-1391 requires the first usable Instance configuration vertical, including Console,
Bootstrap and OpenAPI. Contribution options under `aep.configuration` describe
Model Profiles and Source Channels; they do not configure an extension process.
Future work needs independent Tenant/Workspace policies and explicit cold settings.

## Decision

- Add the optional `aep.operational-configuration` capability, version `1.0`, with
  bounded authenticated GET/PUT exchanges at `/aep/operational-configuration`.
  Configuration is never anonymously available, including when enrollment is disabled.
- An immutable descriptor pins its id, version and SHA-256 digest over option
  metadata. Each option declares its JSON schema, required/protection status,
  allowed binding sources, process/context/binding applicability, application mode
  and optional atomic group. The first executable adapter supports process settings
  with immediate activation; other modes are described but rejected for writes.
- Agentstration persists an Instance-owned `ExtensionConfiguration`, tied to the
  exact registration UID and extension identity. It contains Literal/Parameter/Secret
  bindings and observed activation metadata, never resolved Secret material.
  SQLite and PostgreSQL use the existing generic resource storage envelope.
- Console, REST and Bootstrap use one application service. Instance context and
  normal resource permissions are required. Registration provenance does not make
  separately owned configuration read-only. Descendant overrides are unavailable.
- Standard values are resolved and schema-checked before delivery. Mutable bindings
  are revalidated before functional AEP/MCP use in the configuration owner's context.
  Secret bindings use existing one-use Secret capabilities and a protected callback;
  neither Secret bytes nor hashes of those bytes become ordinary persisted evidence.
- Save desired state before attempting delivery. Record applied generation/revision
  only after matching runtime acknowledgment. A lost response leaves pending state;
  retry can recognize the already active revision. Resource ETags and bounded local
  serialization protect concurrent commands; runtime revision/generation checks reject
  stale activation. This does not promise a distributed transaction or exactly-once I/O.
- OpenAPI can start/enroll with an empty allowlist and denies destinations by default.
  `AllowedDomains` is initially seeded by host configuration, then replaced by the
  locally persisted operational revision. `DomainCeiling`, when configured, remains
  a deployment-owned ceiling. No networking/method/size restrictions are relaxed.
- OpenAPI validates a candidate, atomically persists its non-secret state and swaps
  its active policy. Subsequent destination checks read that policy. Already-sent
  network requests are not claimed to be cancelled by an allowlist change.
- Bootstrap uses existing public installation selectors and dependency waits.
  Preview performs no configuration writes. Reapplication preserves existing
  operator-authored desired values; pending delivery can be retried explicitly.
- JSON-array Parameters (at most 128 elements and the existing byte bound) extend
  the existing typed Parameter model so domain lists need no JSON-in-string binding.
  Console reuses `ValueBindingRequirementEditor`, structured literal editing and
  contextual Parameter/Secret creation.

## Consequences

The first delivery does not wait for descendant inheritance/delegation or all-provider
adoption. FR-1392 through FR-1396 own further adapters, cold activation and scoped
contexts/policies. Unsupported capabilities stay read-only and unsupported activation
modes fail explicitly. No process restart, generic remote appsettings editor, broker
or cloud provisioning authority is introduced. Back up the OpenAPI configuration
state with its enrollment state and the Agentstration control plane. Restoring only
one side requires explicit recovery rather than silently adopting another identity.

The reusable browser journey remains deferred under #1397 until maintainer UX
acceptance and implementation merge. Implementation behavior tests and browser
smoke checks are part of FR-1391.
