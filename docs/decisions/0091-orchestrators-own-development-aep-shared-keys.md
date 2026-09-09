# ADR-0091: Orchestrators own development AEP shared keys

Status: Accepted — 2026-09-09

## Context

Aspire and Docker Compose development need unattended AEP authentication. Interactive pairing is unnecessary for processes launched together, while a global token or writable shared directory would let one compromised extension impersonate another.

## Decision

Enrollment and transport authentication remain separate. `SharedKeyFile` enrollment always selects `StaticBearer` authentication, while `Disabled`, `PairingCode`, and `SharedKeyFile` remain distinct enrollment modes. Manual Extension Registrations cannot select `SharedKeyFile` because only an orchestrator may provision it.

The Aspire AppHost generates one independent 256-bit random token per extension under the ignored slot data directory. Creation uses a temporary file and an atomic non-overwriting move, applies owner-only permissions on Unix, and reuses an existing valid file across restarts. Agentstration and only the matching extension receive that file path.

Docker Compose uses one persistent named volume per extension. A one-shot provisioner is the sole writer; each extension mounts only its own volume read-only, while Agentstration mounts each volume at a distinct read-only path. Coordinated restart after atomic replacement is the initial rotation mechanism.

The file format is bounded to 4096 bytes and contains one strict UTF-8 token line with at least 32 bytes of token material. A final LF or CRLF is accepted; embedded line breaks, empty, short, oversized, malformed, missing, and unreadable files fail closed without logging their contents.

Agentstration projects each configured file into an instance-scoped read-only Secret/Vault provider. Management resources persist only the path and reference metadata; the clear value remains in the orchestrator file and is read through `ISecretResolver` for each request.

## Consequences

- Aspire and Compose authenticate official extension traffic without an interactive step.
- An extension cannot read or replace another extension's key through its mount.
- Removing or corrupting a configured file prevents authenticated startup or invocation with non-secret diagnostics.
- Development key files and volumes are local operational state, not portable Pack or repository content.
