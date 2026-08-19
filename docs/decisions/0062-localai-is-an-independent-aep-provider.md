# ADR-0062 — LocalAI is an independent AEP provider

## Status

Accepted

## Context

Agentstration supports Ollama and llama.cpp through autonomous AEP extensions. LocalAI also exposes OpenAI-compatible Chat Completions, but it is a multi-backend server whose model catalog can contain chat, embedding, image, audio, transcription, and other models. Treating every `/v1/models` entry as chat-capable, or inheriting llama.cpp capability rules, would invent execution support. LocalAI can also inject its own MCP tools through request metadata, which must not bypass Agentstration's governed Tool execution boundary.

## Decision

- `Agentstration.Extensions.LocalAI` is an autonomous AEP model-provider extension with contribution ID `localai`.
- The extension has its own HTTP, JSON, SSE, health, discovery, error, and native-option implementation. It does not reference the llama.cpp extension and no shared OpenAI-compatible abstraction is introduced in this increment.
- Agentstration persists the AEP extension endpoint. `LocalAI:Endpoint` configures the extension's native LocalAI endpoint, and an optional `LocalAI:ApiKey` is supplied only through extension-host configuration.
- Chat uses `/v1/chat/completions`; readiness uses `/readyz`; model discovery requires `/v1/models/capabilities` and exposes only entries that report `chat`.
- Model capabilities are mapped conservatively. `tools` and `thinking` become AEP tool and reasoning support. Streaming is exposed for chat models. Vision and structured output are not advertised because their effective support varies by LocalAI backend and image input is not effective through the current AEP adapter.
- LocalAI-native options are allowlisted. The extension never forwards arbitrary metadata, LocalAI MCP server selection, or provider-owned tool injection. Agentstration tools are sent only from the governed AEP tool definitions on the request.
- Aspire starts the AEP extension and connects it to an existing LocalAI server. It does not provision LocalAI, install a model, or mutate its catalog. The default native host port is `8081` to avoid colliding with the existing llama.cpp default on `8080`.

## Consequences

LocalAI can evolve independently from llama.cpp and Ollama without leaking backend details into Management or Runtime. Capability-safe discovery requires a LocalAI version that implements `/v1/models/capabilities`. Older servers are reported as incompatible instead of exposing an ambiguous heterogeneous catalog. Some transport code is intentionally duplicated; a shared OpenAI-compatible adapter may be considered later after the independent implementations establish stable common behavior. LocalAI API keys are not persisted in committed configuration or provider options, and full Vault-to-extension secret delivery remains a separate concern.
