# ADR-0100: AEP unenrollment is an explicit recoverable transition

- Status: Accepted
- Date: 2026-09-10

## Context

ADR-0093 deliberately made credential revocation terminal: a revoked PairingCode installation remains closed until an operator resets its local state. That remains the correct response to a compromised credential, but it does not cover the ordinary administrative need to remove a healthy trust relationship and perform the enrollment ceremony again.

Treating registration deletion, connection disablement, credential rotation, revocation, and unenrollment as synonyms would either leave active credential material behind or turn recoverable maintenance into a local file operation. It could also create a second inventory row when the same stable extension instance announces again.

## Decision

Unenrollment is a separate, authorized Management use case. It targets the existing enrollment request by stable extension instance ID and preserves its assigned Instance, Tenant, or Workspace scope. The transition is idempotent, audited, and represented by `Unenrolling` followed by `Unpaired` so an interrupted cleanup can be retried safely.

For PairingCode, Agentstration calls a dedicated extension lifecycle endpoint with the current credential. The extension atomically returns the same installation instance to `unpaired`, removes its client identity and active digests, and retains the old digests only as a bounded revocation list. Repeating the same request with that old credential is accepted only by the unenrollment endpoint; it cannot authenticate any functional AEP endpoint. Agentstration then removes the local Vault value and disables the generated Extension Registration. A new one-time code creates a new credential and re-enables the same deterministic registration instead of creating another inventory entry.

For SharedKeyFile, the orchestrator remains the credential lifecycle boundary defined by ADR-0091. Unenrollment disables the materialized registration and returns the request to `Unpaired`; it does not modify or delete the orchestrator-owned file. Re-enrollment explicitly provisions and enables the same scoped registration from the already validated announcement. Rotating or destroying the shared key itself still requires the orchestrator workflow.

Revocation remains terminal and distinct. It is used when trust must stay closed, while unenrollment is used when a new enrollment is expected. Neither authentication failure nor extension restart triggers unenrollment automatically.

## Consequences

- Operators can repeat enrollment without deleting state files or creating duplicate registrations.
- Old PairingCode credentials are rejected immediately by functional endpoints.
- An interrupted PairingCode unenrollment can safely retry its extension callback with the previous credential.
- SharedKeyFile unenrollment detaches Agentstration from the credential but cannot invalidate copies of an orchestrator-owned key outside Agentstration.
- Scope authorization, security audit redaction, and enrollment-mode policy continue to apply to re-enrollment.
