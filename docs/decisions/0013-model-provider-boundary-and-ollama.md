# ADR-0013: Model-provider boundary and local Ollama adapter

## Status

Accepted — 2026-08-01

## Context

Agentstration needs a first executable local model-provider integration while preserving the distinction between technical agent execution and model connectivity. Ollama must remain optional, must not leak into Management or Runtime abstractions, and must not replace the offline deterministic default.

## Decision

- `Agentstration.ModelProviders` contains the provider-neutral `IModelProvider` and resolver contracts.
- `Agentstration.ModelProviders.Ollama` owns OllamaSharp registration and exposes the selected local model as the standard `Microsoft.Extensions.AI.IChatClient`.
- `Agentstration.Web` is the composition root. It activates the Ollama adapter only when `AI:Provider` is `Ollama`.
- `Agentstration.Runtime.AgentFramework` continues to consume only `IChatClient`; it does not reference Ollama or Aspire.
- `Agentstration.AppHost` alone provisions the Ollama container, persistent model volume, and development model through Aspire.
- Direct Web startup remains deterministic by default and can target an independently running Ollama instance through configuration.
- A development-only HTTP diagnostic validates connectivity. Normal agent validation uses the existing Agent Runner and Runtime Run path.

## Consequences

The model provider and runtime adapter can evolve independently, tests remain offline, and local Docker orchestration is optional. The first implementation intentionally supports one configured Ollama model per host; multi-model catalogs and Management model-profile resources remain separate future work.
