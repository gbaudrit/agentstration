# ADR-0046: Platform administration is explicitly transferable

Status: Accepted — 2026-08-16

## Context

Bootstrap grants the first local Principal the instance-level `PlatformAdmin` authorization. That grant must not make the bootstrap account permanent: an operator needs to transfer administration to another local or externally authenticated Principal and then disable or retire the original account.

`PlatformAdmin` is distinct from Workspace ownership and administration. Inferring it from a Workspace role would leak authorization across Workspace boundaries. Conversely, an unsafe revoke or disable operation could leave a local-first instance without an administrator able to recover it.

## Decision

The Management Plane owns the Platform administrator lifecycle. An active Platform administrator can list grants, grant the role to any active Principal, and revoke another Principal's grant. Granting an existing assignment and revoking an absent assignment are idempotent.

The lifecycle service enforces these invariants independently of the HTTP or Console surface:

- Workspace roles never imply `PlatformAdmin`;
- a disabled Principal cannot receive a Platform administrator grant;
- an administrator cannot revoke their own grant or disable their own Platform administrator account;
- an active Platform administrator cannot be disabled or revoked unless another active Platform administrator exists;
- disabling an administrator retains the grant, so re-enabling that Principal restores its instance authorization unless the grant is separately revoked.

The supported handover is therefore: grant the successor, have the successor authenticate, then let the successor disable and, when appropriate, revoke the predecessor. There is no default successor and no recovery credential.

`GET`, `PUT`, and `DELETE /api/identity/platform-administrators/{principalId?}` expose the lifecycle under the standard ASP.NET Core `PlatformAdmin` policy. Console **Organization > Members > Member details** delegates to the same Management service. Grant and revoke operations append identifier-only records to the security audit log.

The current standalone deployment serializes lifecycle mutations with an in-process lock. Authorization decisions still use durable Management data; the lock only closes concurrent last-administrator checks inside the single authoritative server described by ADR-0032.

## Consequences

- The bootstrap account can be safely replaced without a cloud provider or manual database changes.
- A local or external identity is eligible because the grant targets the common Agentstration Principal, not an authentication account type.
- Self-lockout through the supported application surfaces is rejected.
- Retaining a disabled Principal's grant separates account suspension from role revocation and keeps both actions auditable.
- Multiple independently writing server replicas would require a database constraint, serializable transaction, or single-writer coordinator for the last-administrator invariant. That topology is outside the current standalone architecture.
- Emergency recovery after loss of every administrator credential is deliberately not implemented as a hidden bypass; an explicit offline recovery design remains future work.
