# ADR-0093: AEP enrollment lifecycle is explicit and audited

- Status: Accepted
- Date: 2026-09-09

## Context

A paired extension remains a workload identity after its first successful enrollment. Restarting either process must not reopen enrollment, and replacing or revoking its credential must not create an authentication gap or silently turn an authentication failure into a new pairing opportunity.

## Decision

Pairing state is durable on both sides. Agentstration records the workspace-bound request and the extension stores its stable instance ID, state, client ID, and credential digests. Rotation first installs a new digest while retaining the previous digest, persists and verifies the new Agentstration Secret, and only then explicitly removes the previous digest. Revocation closes the extension locally, removes the Secret value, disables its registration, and records `Revoked`; it never returns to `Unpaired` automatically. Registration disablement is represented separately as `Disabled` and is effective for subsequent resolution.

Enrollment transitions append bounded security-audit events. The extension instance ID occupies the audit target-ID field; tenant/workspace, actor when applicable, outcome, stable reason code, trace correlation, and timestamp use the existing durable audit contract. Events never include pairing codes, Bearers, Secret values, prompts, tool arguments, or remote response bodies.

The extension SDK exposes an explicit local `AepPairingLifecycle.ResetToUnpaired(stateFile)` recovery operation. It preserves the installation instance ID and removes all credential material. Operators must invoke it locally; network authentication failure never invokes it.

## Consequences

- Rotation is zero-downtime and the old credential has an explicit end point.
- Revoked installations remain closed across restarts and reject the credential on the next request.
- Authentication, authorization, protocol-version, reachability, identity, expiry, consumption, cancellation, and rate-limit failures retain distinct stable codes.
- Backup must include the Agentstration control plane, Local Vault key material, and the extension pairing-state file as one consistency set.
