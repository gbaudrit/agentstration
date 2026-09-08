# ADR-0097 — Compose owns its local Ollama service

## Status

Accepted

## Context

The first Compose AEP topology connected the Ollama extension to an inference server outside Docker. With a native Docker Engine running inside WSL, a Windows Ollama process bound to `127.0.0.1` is not reachable from the Docker network. Bridging that boundary requires a mutable Windows relay or a wider Ollama listener, complicates local setup, and can require firewall administration.

Compose is also useful for comparing inference performance in a reproducible Linux container without changing the independently supported direct and Aspire launch paths.

## Decision

- The provider-specific `deploy/compose/ollama.yml` topology includes the Ollama AEP extension, the Utilities extension, a version-pinned Ollama service, and a persistent named volume for its model store. The base Compose topology remains minimal and deterministic; llama.cpp and LocalAI have separate Compose files.
- The Ollama AEP extension uses the Compose service name and internal port. It does not traverse the Windows/WSL host boundary.
- No Ollama port is published to the host. Operators use `docker compose -f deploy/compose/ollama.yml exec` for direct diagnostics and model management.
- Compose never pulls a model implicitly. Model selection and installation remain explicit operator actions.
- GPU devices are not declared in the default topology. The default must start when `/dev/dri` is absent. Operators may add a device mapping through an environment-specific override after verifying GPU passthrough on the Docker host.
- Aspire continues to consume an operator-owned Ollama installation as decided by ADR-0029. Direct Web startup and the deterministic fallback are unchanged.

## Consequences

The Compose Ollama path no longer needs `host.docker.internal`, a Windows port proxy, or a Windows Firewall exception. Its model cache is separate from the native Windows Ollama cache and consumes additional disk space. CPU inference works wherever the image runs; GPU acceleration depends on the Docker host exposing a supported device and must be verified through `ollama ps`.

This supersedes ADR-0096 only where that decision excludes inference-server provisioning from the Compose topology.
