# ADR-0014: Configuration-backed model resolution into MAF

## Status

Accepted — 2026-08-02

## Context

Declarative agents already reference a provider-neutral `modelProfile.resourceId`, while the first Ollama increment exposed one local `IChatClient`. Runtime materialization still resolved every profile to that same client, so the declared profile did not drive provider or model selection.

## Decision

- `Agentstration.ModelProviders` owns the minimal profile, deployment, and provider configuration records and their store contracts.
- Configuration-backed stores implement the first executable catalog. A profile resource ID resolves by its canonical final name.
- Provider endpoints may come from a normal endpoint setting or a named connection string. Aspire injects the `local-chat` connection; business and Runtime code do not depend on Aspire types.
- `IChatClientResolver.ResolveAsync` performs profile → deployment → provider configuration → technical provider resolution.
- `Agentstration.Runtime.AgentFramework` injects this provider-neutral resolver, builds the MAF agent with the resolved `IChatClient`, and never references OllamaSharp.
- The existing durable Runtime Run API and Agent Runner are the validation surface. No parallel synchronous agent-execution API is introduced.
- The deterministic default retains a simple resolver so offline startup and tests do not require configured model profiles.

## Consequences

The canonical agent remains provider-neutral and the temporary configuration stores can later be replaced by Management Plane persistence. This increment supports one local Ollama model, no tools, no Flow execution, no session persistence, and no multi-provider routing or fallback.
