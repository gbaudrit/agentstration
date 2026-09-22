# ADR-0128: Foundry non-streaming chat uses the existing AEP runtime path

Status: Accepted — 2026-09-17

## Context

ADR-0119 introduced an isolated Foundry AEP extension with deployment discovery but no inference. FR-446 must use a selected Model Profile through Agentstration's durable Runtime and Flow paths. The configured inference endpoint may be resource-routed (`/openai/v1`) or project-routed (`/api/projects/{project}/openai/v1`), and these routes require different Microsoft Entra token audiences. Runtime Runs default to automatic streaming, whereas this increment cannot stream.

## Decision

- Implement bounded OpenAI/v1 Chat Completions in the Foundry extension's AEP `ChatAsync` contribution. The central Model Provider, `IChatClient`, Runtime and Flow boundaries remain provider-neutral.
- Send the selected deployment name as `model`. Support text system, user and assistant messages, successful tool-result history, and the canonical generation options supported by this increment. Reject explicit unsupported options before making an external request; do not silently drop them.
- Use the configured resource or project inference route without automatic redirects. Project discovery and project-routed inference request `https://ai.azure.com/.default`; resource-routed inference requests `https://cognitiveservices.azure.com/.default`. API-key mode continues to use the temporary extension-local key pending FR-439/FR-452.
- Normalize text, token usage and finish reason into AEP; keep the selected deployment name as the returned AEP model identity even if Foundry reports an underlying model ID. Treat content-filter and unsupported tool-call responses as explicit failures. Do not expose provider response bodies in errors.
- When Runtime streaming is `Automatic` and the effective provider/model capability is unsupported, execute non-streaming. Explicitly requested streaming still fails compatibility validation. This rule is provider-neutral.
- Keep request/response sizes and elapsed time bounded. The default offline test suite uses fake HTTP responses and does not call Azure.

## Consequences

Foundry can answer an existing text Agent or Flow through the governed AEP/Runtime path without introducing a Foundry-specific execution API. A Model Profile that requests tools, structured output or explicit reasoning still cannot use those features until their later increments. Local live inference is opt-in and may incur charges. FR-449 remains the security and observability gate before a broader orchestrated or remote preview.
