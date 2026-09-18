# ADR-0131: Foundry egress and diagnostics are bounded

Status: Accepted — 2026-09-18

## Context

The optional Foundry AEP extension sends authenticated discovery and chat requests to configured endpoints. FR-449 gates broader orchestrated and remote preview on explicit egress, retry and diagnostic behavior. A continuation URL, DNS answer, process proxy or interrupted response must not bypass the endpoint policy or expose credentials.

## Decision

- Validate the HTTPS origin, path and method immediately before applying a key or Entra token. Discovery continuations remain on the configured deployment path. The transport disables redirects and process proxies, resolves each new connection, and connects only to an allowed resolved address. Link-local, multicast and reserved endpoints stay blocked; configured hosts may explicitly use private or shared address space.
- Keep discovery pages, response bytes, model count, request time and chat/stream payloads bounded. Retry a read-only discovery page at most once on a transport failure or HTTP 429/502/503/504, within the same page timeout. A chat POST or stream is never replayed, including after its first event.
- Return stable AEP codes for authentication, authorization, missing project/model, invalid request, timeout, throttling, service failure, malformed response and content filtering. Do not surface provider bodies. Emit structured Foundry operation diagnostics with deployment identity, outcome, HTTP status, retry count, duration and token usage. Do not log prompts, Tool arguments, credentials or provider bodies.

## Consequences

Private endpoints require an explicit host allowance. Environment proxy routing is unavailable for this extension. Discovery may perform one extra bounded request during a transient failure. The default offline path remains unchanged; FR-451 supplies the remaining deployment and Console workflow, and FR-452 replaces the temporary key path after FR-439.
