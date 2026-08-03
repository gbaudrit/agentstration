# ADR-0017: Canonical runtime, model options, and effective capabilities

## Status

Accepted — 2026-08-03

## Context

The first model-provider vertical resolved a persisted model profile into an `IChatClient`, but its durable profile only carried temperature and output-token limits. Runtime execution returned one string and exposed no canonical streaming events or effective-capability model. That was insufficient for provider-specific behavior such as Ollama reasoning while keeping Microsoft Agent Framework (MAF) replaceable.

Because the product is still V1, the former `options` payload is removed instead of being read as a compatibility alias. Existing development control-plane databases must be reset and reseeded.

## Decision

- Management resources own canonical generation, reasoning, and output categories. Provider-native values remain under a provider-keyed `providerOptions` section.
- `RuntimeProfileResource` is an independent persisted Management resource. It describes session, tool invocation, streaming defaults, and runtime-keyed options; it does not describe model behavior.
- A deployment records the agent and model-profile references resolved by its immutable revision together with its runtime-profile reference.
- Runtime abstractions own execution options, normalized events, and capability contracts. They do not expose MAF types. Tool catalog entries are canonical `IAgentTool` values and are translated only by the MAF adapter.
- Effective capabilities are the intersection of provider, model, runtime, and concrete adapter levels. Validation reports the requested capability, provider, model, runtime, and effective support before provider invocation.
- Options are merged independently by category and in caller-supplied precedence order. A global JSON merge is forbidden.
- The MAF adapter maps canonical generation/output/reasoning intent to `ChatOptions`, selects streamed or non-streamed MAF execution, and emits Agentstration events.
- The Ollama adapter parses typed native options, retains `additionalOptions`, and applies native options after generic intent. Its MAF-compatible chat client rejects `endpointMode=generate`.

## Consequences

MAF remains an execution adapter and Ollama remains a model-provider adapter. Existing profiles using the legacy `options` shape remain readable during migration, while new profiles use the canonical categories. Reasoning support through the current MAF/Ollama client is deliberately reported as partial because generic intent and native Ollama levels are not guaranteed to have a lossless transport for every model and client version.
