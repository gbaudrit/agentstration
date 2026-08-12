# ADR-0029: Aspire consumes an existing local Ollama installation

## Status

Accepted — 2026-08-11

## Context

The AEP Ollama extension is an Agentstration-owned autonomous service, but Ollama itself is already installed and managed on the developer workstation. Provisioning another Ollama server, persistent volume, and model through Aspire duplicates ownership, consumes unnecessary resources, and can expose a different model catalog from the one used outside Aspire.

## Decision

- `Agentstration.AppHost` orchestrates `Agentstration.Extensions.Ollama` but does not provision an Ollama container, data volume, or model.
- The extension connects to the existing installation through `Ollama:Endpoint`, defaulting to `http://localhost:11434`.
- Operators can override the endpoint with normal .NET configuration, including the `Ollama__Endpoint` environment variable.
- Model installation and lifecycle remain owned by the local Ollama installation. Agentstration only discovers and invokes models through its AEP extension.
- Deterministic execution remains the offline default and does not require Ollama to be running.

## Consequences

Aspire no longer requires the Ollama hosting integration or Docker for this path. Starting the AppHost does not download a model or mutate the local Ollama model catalog. The Ollama extension can start while Ollama is unavailable, but provider health, discovery, and invocation report that unavailability until the configured local server responds.

This decision supersedes ADR-0013, ADR-0018, and ADR-0026 only where they assign Ollama server provisioning to Aspire. Their model-provider, persistence, and out-of-process AEP boundaries remain accepted.
