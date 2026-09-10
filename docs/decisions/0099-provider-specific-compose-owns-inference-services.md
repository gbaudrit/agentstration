# ADR-0099 — Provider-specific Compose owns inference services

## Status

Accepted

## Context

The canonical Compose topology starts the official AEP extensions with isolated SharedKeyFile credentials but leaves inference servers operator-owned. With Docker running natively inside WSL, inference processes bound to the Windows loopback are not reachable from the Docker network without a relay, wider listener, or firewall configuration.

Provider-specific container topologies are also useful for comparing inference behavior while keeping model stores and lifecycle boundaries explicit.

## Decision

- Compose assets live under `deploy/compose`; `base.yml` retains the canonical deterministic extension topology.
- `ollama.yml`, `llama-cpp.yml`, and `localai.yml` each start Agentstration, Utilities, the selected model-provider extension, and its inference service on one internal network.
- Every AEP extension uses the isolated provisioner-owned SharedKeyFile volume and StaticBearer transport established by ADR-0091. Provider topologies reuse the canonical `Dockerfile.extension`.
- Ollama and LocalAI use persistent named volumes. llama.cpp reads an explicitly selected GGUF file from an ignored host directory mounted read-only.
- Compose never downloads a model implicitly. Model acquisition and license acceptance remain explicit operator actions.
- No inference or extension port is published. Agentstration is the only host-facing service.
- CPU-capable images are the safe defaults. GPU-specific images and device mappings remain explicit environment-specific overrides.
- `postgresql.yml` is a shared storage overlay that can be combined with `base.yml` or any provider-specific topology.
- Aspire continues to consume operator-owned inference servers as decided by ADR-0029, ADR-0061, and ADR-0067.

## Consequences

Provider-specific Compose launches no longer need `host.docker.internal`, a Windows relay, or a Windows Firewall exception. Each provider keeps a separate model cache, while the common PostgreSQL overlay avoids duplicating storage configuration. llama.cpp startup fails clearly until the configured GGUF file exists. Image and model versions must be updated deliberately.
