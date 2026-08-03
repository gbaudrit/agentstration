# ADR-0016: Real model invocation from Agent Runner

## Status

Accepted — 2026-08-02

## Context

The Runtime Run vertical already delegated execution to Microsoft Agent Framework, but the Blazor Runner could still resolve simulated clients. Model-profile defaults and console overrides were not propagated to `ChatOptions`, and the Ollama adapter rejected every model except the host startup default. Mutable profile changes could also leave an already provisioned runtime bound to stale model resolution.

## Decision

- Agent Runner uses dedicated canonical HTTP clients even when unrelated dashboard projections use simulated data.
- Runtime exposes readiness and an explicit idempotent local preparation operation for an exact agent generation.
- Preparation creates or reuses the exact immutable revision and a generation-specific local deployment, then reconciles it synchronously.
- `AgentFrameworkRuntime` resolves the current model profile for every execution and constructs the reconstructible `ChatClientAgent` at the execution boundary.
- Profile temperature and maximum-output-token defaults flow through model metadata into MAF `ChatOptions`.
- Runtime overrides are limited to `temperature` and `maxOutputTokens`, validated at both UI and Runtime boundaries, and take precedence over profile defaults.
- The Ollama adapter pins each request to the deployment model through `ChatOptions.ModelId`; it no longer requires that model to equal the host default.
- Runs record the actual provider, model, and effective supported options. Prompts and responses remain excluded from logs.

## Consequences

The console Run button now exercises the durable Runtime API and the actual configured provider. Profile changes take effect on the next Run without persisting provider-specific agents. Dashboard simulation remains available, provider-native token streaming remains a later increment, and remote evaluation still requires an explicitly configured provider.
