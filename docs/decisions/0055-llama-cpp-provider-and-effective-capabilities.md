# ADR-0055 — llama.cpp is an AEP provider and capabilities are resolved effectively

## Status

Accepted

## Context

Ollama already runs behind an autonomous AEP extension, but some startup aliases, UI copy, and compatibility validation still mentioned Ollama in generic layers. AEP provider and model descriptors advertised capabilities without carrying them into Runtime execution, so the existing provider/model/runtime/adapter intersection was exercised only by unit tests. Adding llama.cpp must not recreate an in-process provider hierarchy or claim features that the selected server and model cannot execute.

## Decision

- `Agentstration.Extensions.LlamaCpp` is an autonomous AEP model-provider extension. Agentstration persists the extension endpoint; `LlamaCpp:Endpoint` configures the extension's native `llama-server` endpoint.
- The extension uses llama.cpp's OpenAI-compatible `/v1/chat/completions` and `/v1/models` routes plus native `/health` and `/props` inspection. It does not expose native `/completion` as chat.
- Model capabilities are observed dynamically. Tool calling and reasoning are advertised for a selected model only when `/props` exposes matching chat-template capabilities. Structured output and streaming are native server capabilities. Current AEP image transport is not effective, so the extension does not advertise provider-level vision support.
- `IModelProviderCapabilitiesResolver` resolves provider, selected-model, and concrete adapter capabilities with the client. `ModelChatClientMetadata` carries those levels to Runtime.
- Microsoft Agent Framework intersects provider, model, AEP-to-`IChatClient`, and runtime capabilities before invocation. Requested streaming, tools, structured output, or reasoning that is effectively unsupported fails before the provider call.
- Provider-native validation remains in its extension. The Ollama `endpointMode=generate` rule is removed from the generic compatibility validator.
- `AI:Provider=Managed` is the single persisted-profile mode. Generic composition does not branch on concrete provider identifiers.
- Aspire orchestrates the llama.cpp AEP extension but only connects to an existing `llama-server`. It does not provision llama.cpp, Docker, or GGUF models.

## Consequences

Ollama and llama.cpp are independent implementations of AEP's functional provider contract, while MAF remains unaware of both. Resolution performs dynamic discovery before execution, adding local HTTP calls but preventing stale or invented capability claims. A future OpenAI-compatible extension may extract the common Chat Completions transport after another backend demonstrates the same requirements; llama.cpp-specific health, properties, and native options remain explicit for now.
