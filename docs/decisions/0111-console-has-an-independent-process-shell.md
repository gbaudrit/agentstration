# ADR-0111 — The operations Console has an independent process shell

## Status

Accepted

## Context

ADR-0032 and ADR-0110 made `Agentstration.Web` the sole executable host while the operations Console UI was extracted into client and component libraries. The secure Console BFF roadmap needs a process boundary before delegated trust, token exchange, or deployment topology can be implemented safely. Moving authoritative APIs or workers at the same time would mix the process-shell concern with security and topology decisions.

## Decision

Add `Agentstration.Console.Web` as an independently runnable ASP.NET Core host for the operations Console. It references only the Console client/components, shared UI, Flow designer UI, and their neutral contracts. It hosts the existing operations routes with interactive server rendering and owns its browser-facing cookie authentication registration, antiforgery, culture selection, return-URL convention, static assets, observability, and liveness/readiness endpoints.

Management, Work, Flow, and Runtime API origins are configured independently. The host does not forward the standalone application cookie, issue internal API tokens, validate internal tokens, or own an authoritative API, store, worker, scheduler, or business service. Those trust and deployment capabilities remain separate roadmap increments. `Agentstration.Web` continues to provide the supported all-in-one authenticated experience and retains same-origin cookie forwarding.

Console-specific static assets live with `Agentstration.Console.Components` so both executable shells consume one asset implementation.

This decision extends ADR-0043 for the separated-host case and supersedes the sole-executable statement in ADR-0110. It does not change the modular-monolith ownership of API modules or data.

## Consequences

The Console can now be launched and probed as a separate process without loading authoritative server implementations. Architecture tests enforce the dependency boundary, and host tests verify route/static-asset hosting, separate API origins, browser-session conventions, and health endpoints while the authoritative server is unavailable.

The independent shell is not yet a production-ready security boundary: authenticated downstream operations require the later delegated-token and interactive-session increments. The standalone executable remains operational throughout the migration.
