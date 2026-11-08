# ADR-0202: Durable Flow inputs retain live Secret references

## Status

Accepted — maintainer clarification for [#1044](https://github.com/gbaudrit/agentstration/issues/1044) and [#1083](https://github.com/gbaudrit/agentstration/pull/1083), 2026-11-08.

## Context

ADR-0201 refused Secret-bearing Flow submissions because resolving a Secret into ordinary JSON would retain its value in Work inputs, Flow Run inputs and step state. Secrets are a required part of the relay capability. The maintainer requires durable references and resolution of the current value on execution and recovery, including after rotation.

## Decision

- Supersede ADR-0201's temporary refusal of Secret inputs to durable Flows. Reuse the existing scoped SecretReference, UID identity checks and Secret resolver. Freeze the referenced identity, never the value.
- A relay creates a stable symbolic input identifier and a trusted reference carrying the Secret identity and consumer context. JSON inputs carry symbolic markers. Work submission retains the references as metadata; the Flow Run retains its trusted reference set. Public request JSON and caller-supplied metadata cannot establish this authority.
- Pass symbolic inputs through mappings, stored step state and child Flows. Load trusted references from the persisted Flow Run on the server for each Tool attempt. The Worker and execution-material documents receive symbolic inputs, never materialized Secret values. Secret references are opaque to ordinary Flow expressions; they may be forwarded to Tool inputs without exposing their values to models or transforms.
- Resolve current values only at the server execution boundary, rechecking exact UID, scope, authorization, availability and the complete Tool input contract. New attempts and recovered runs resolve again; rotation therefore affects subsequent attempts. Completed steps are not automatically repeated. Missing, replaced or revoked resources fail explicitly without fallback to an earlier value.
- Suppress argument capture for protected calls. Replace exact Secret echoes with symbolic references and redact embedded echoes before returning outputs to the Worker. Sanitize protected failures before they become stored errors or diagnostics. Apply the same rule to Knowledge Tool dispatch and nested relays.
- Relay simulation checks the exact source and live inputs, and evaluates its governance through validation-only pipeline execution. It never invokes the source implementation, including a provider's business operation.
- Usages enumerates the existing authorized scope inventory and filters it to the definition's owner and descendants. Resource and Flow reads retain their normal context authorization. Each result identifies the actual consuming scope. Never use a system inventory to disclose unauthorized consumers.

## Consequences

Flow Run documents gain an optional reference collection; existing documents default to an empty collection. SQLite and PostgreSQL retain their existing document storage model. Work inputs, recovery material, steps, events and outputs must be tested for absence of resolved values, alongside recovery after rotation, revoked or replaced references and invalid current values.

Existing accepted ADR text is retained. Developer and operator documentation describes this decision instead of the superseded limitation. The new reusable browser scenario remains gated under #1050; this decision does not grant implemented-UX approval or merge authorization.
