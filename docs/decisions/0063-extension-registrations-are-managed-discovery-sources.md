# ADR-0063 — Extension registrations are managed discovery sources

## Status

Accepted

## Context

Configuration and Aspire service discovery can expose known AEP endpoints, but changing application configuration is not a practical administration workflow. The Console also needs to distinguish the desired set of endpoints to inspect from the live extension inventory returned by AEP discovery. Automatic network scanning would be unsafe, non-deterministic, and incompatible with workspace isolation.

## Decision

- A manually declared endpoint is persisted as a workspace-owned, namespace-scoped `ExtensionRegistration` Management resource.
- A registration contains a display name, an absolute HTTP(S) endpoint, and an enabled flag. Endpoint credentials, query strings, and fragments are rejected. Two registrations in one namespace cannot target the same normalized endpoint.
- Registration writes use the existing control-plane ETag and generation semantics. Disabled resources remain persisted but no longer feed extension discovery.
- The observed `/api/extensions` inventory merges enabled Management registrations, application configuration and Aspire-injected endpoints, then de-duplicates endpoints before inspection. `/api/extensionregistrations` remains the separate desired-state CRUD surface.
- Configuration and Aspire registrations are read-only in the Console. No source scans the local network, and a registration does not imply extension installation or process lifecycle management.

## Consequences

Operators can add, edit, disable, and remove extension discovery targets without restarting Agentstration. Registrations inherit control-plane persistence and workspace isolation without a provider-specific store. The platform can clearly show desired registrations even when their extension is offline, while retaining configuration as a local-first bootstrap mechanism. Installation, credentials, health polling, and upgrade orchestration remain separate concerns.
