# ADR-0094: AEP extensions initiate enrollment

- Status: Accepted
- Date: 2026-09-09

## Context

Startup discovery made Agentstration create registrations from configured endpoints before an extension established its enrollment identity. PairingCode already used the opposite direction: an extension announced itself when the authority became available. Keeping a pull-based SharedKeyFile path produced duplicate logical connections and made the orchestrator's endpoint configuration authoritative instead of the running extension.

An unauthenticated SharedKeyFile announcement cannot be trusted. Accepting only an extension ID would let another process substitute its own endpoint and cause Agentstration to send the configured credential there.

## Decision

Both PairingCode and SharedKeyFile extensions initiate enrollment and retry their announcement with bounded exponential backoff while Agentstration is unavailable. Every installation retains a stable instance ID in a local state file. The announcement contains the selected enrollment method, extension identity, and public endpoint. It contains no Agentstration Tenant, Workspace, or resource-scope identifier. Only PairingCode includes a same-origin browser pairing URI.

A SharedKeyFile extension signs a canonical, timestamped announcement with HMAC-SHA256 using the configured shared key. It sends only the signature and timestamp, never the shared key. Agentstration accepts a proof for five minutes, compares it in constant time, and binds the signed endpoint to the configured extension identity.

Every new announcement is stored once as an unassigned instance-level candidate. A platform administrator assigns it to exactly one Instance, Tenant, or Workspace scope. Scope existence and write permission are validated by Agentstration. SharedKeyFile materializes its read-only Secret and Extension Registration in that scope after assignment; PairingCode permits code issuance only after assignment. Claims and readiness resolve the stored target and never accept a caller-supplied scope identifier.

Automatic configuration/Aspire endpoint discovery is disabled by default and by the AppHost. Configuration still declares the SharedKeyFile path and allowed enrollment mode, but the running extension supplies its endpoint. Deployments that still depend on legacy configured registrations can explicitly enable the internal startup compatibility pass. No Console action or public HTTP command exposes that synchronization.

The Aspire AppHost starts the Agentstration authority and bundled extensions concurrently. Extensions do not carry an Aspire startup dependency on the Console: their retry policy bridges authority initialization and later outages without leaving project resources blocked in Aspire or relying on debugger-sensitive delayed launches.

## Consequences

- A running extension is the source of its public AEP endpoint for both supported enrollment modes.
- Extension configuration is independent of Agentstration Tenant and Workspace identifiers.
- Instance candidates are visible only to platform administrators until assignment; afterward visibility follows normal scope inheritance.
- SharedKeyFile has no pairing form, one-time code, credential rotation, or revocation action; its orchestrator-owned file remains the credential lifecycle boundary.
- Capturing an announcement does not reveal the shared key and cannot substitute another endpoint because the endpoint is signed; timestamp validation bounds replay.
- Resetting extension instance state creates a new enrollment identity and is an explicit local operation.
- Existing configuration-created registrations remain persisted until explicitly removed or migrated; disabling startup discovery does not delete durable resources.
- Aspire carries no startup dependency in either direction between the Console and extensions; enrollment retry is the readiness coordination mechanism.
