# ADR-0098 — Provider-specific Compose owns inference services

## Status

Accepted

## Context

The provider-specific llama.cpp and LocalAI Compose files initially started only their AEP extensions and connected those extensions to inference servers outside Docker. This retained the same Windows/WSL reachability problem that motivated the container-owned Ollama topology and made the provider files incomplete standalone launch paths.

Official CPU-capable images exist for both inference servers. llama.cpp requires an operator-supplied GGUF model, while LocalAI can start with an empty persistent catalog and install models explicitly.

## Decision

- `docker-compose.llama-cpp.yml` starts a pinned official llama.cpp server image, its AEP extension, Utilities, and Agentstration on one internal network.
- llama.cpp reads one explicitly selected GGUF file from an ignored host directory mounted read-only. Compose never downloads that model.
- `docker-compose.localai.yml` starts a pinned official LocalAI image, its AEP extension, Utilities, and Agentstration on one internal network.
- LocalAI persists model and application state in named volumes. Its catalog starts empty and model installation remains an explicit operator action.
- The AEP extensions address their inference services by Compose service name. Neither topology uses `host.docker.internal`, `host-gateway`, or a published inference port.
- CPU-capable images are the safe defaults. GPU-specific images and device mappings remain explicit environment-specific overrides.
- Aspire continues to consume operator-owned inference servers as decided by ADR-0061 and ADR-0067.

## Consequences

Each provider-specific Compose file is a self-contained container topology apart from explicit model acquisition. Switching providers does not require Windows relays or firewall changes. Model data consumes separate disk space for each provider, and llama.cpp startup fails clearly until the configured GGUF file exists. Image versions must be updated deliberately.

This supersedes ADR-0096 only where it excludes inference-server provisioning from Compose.
