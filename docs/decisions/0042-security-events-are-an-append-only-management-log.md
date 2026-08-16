# ADR-0042: Security events are an append-only Management log

Status: Accepted — 2026-08-16

## Context

Authentication credentials live in the dedicated ASP.NET Core Identity store, while Principals, Platform administrators, Workspace memberships, and role assignments belong to the Management Plane. Security-relevant operations cross both boundaries and require a durable, provider-neutral history without copying credentials or identity-provider payloads into the business model.

Ordinary application logs are not sufficient: their retention and sinks are deployment concerns, they can be filtered, and they are not an authorization-protected product history.

## Decision

Agentstration persists structured `SecurityAuditEvent` records as an append-only table in the Management Control Plane database. The records have no foreign keys, so later account or Principal lifecycle operations cannot erase historical events through cascading deletion.

Each event contains only an action, outcome, actor and target identifiers when known, Tenant/Workspace scope when applicable, a bounded reason code, an OpenTelemetry trace correlation identifier, and an occurrence timestamp. Usernames, email addresses, claims, tokens, passwords, password-policy errors, request bodies, and arbitrary free-form details are excluded.

Authentication and authorization services emit events through the provider-neutral `ISecurityAuditWriter`; HTTP endpoints and Razor components do not construct audit records. The first event set covers local bootstrap, the initial Platform administrator grant, local login/logout, account creation and status changes, password changes, session revocation, and Workspace membership changes.

The Control Plane SQLite adapter stores UTC ticks for indexed chronological ordering. `GET /api/identity/audit-events` and Console **Organization > Security audit** expose at most the latest 200 events and require the instance-level `PlatformAdmin` policy. The application service repeats the Platform administrator check as defense in depth.

## Consequences

- Security history remains local-first and needs no external telemetry or SaaS service.
- Credential and business authorization stores remain separate; the audit log records identifiers only.
- Deleting a future account or Principal will not cascade into historical audit events.
- Failed unauthenticated login attempts can be recorded without persisting the submitted username.
- Audit writes currently participate in the calling operation by failure propagation, but mutations spanning Identity and Management are not one distributed transaction.
- This first bounded read model has no cursor pagination, retention policy, archive/export, cryptographic sealing, or SIEM sink. Those are explicit future increments rather than implied guarantees of tamper evidence.
