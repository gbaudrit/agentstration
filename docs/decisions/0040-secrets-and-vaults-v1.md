# ADR-0040 — Secrets and Vaults V1

Status: Accepted

## Context

Model providers, tools, MCP integrations, agents, Flows, and AEP extensions need credentials without embedding secret values in declarative resources. Agentstration must remain local-first and offline-capable while preserving Management and Runtime boundaries.

## Decision

- `Secret` and `Vault` are canonical workspace-scoped Management resources. A Secret contains metadata, an opaque key, and a relative `ResourceReference` to a Vault; it never contains a value.
- Cross-workspace Secret and Vault references are rejected in V1.
- `Agentstration.Secrets.Abstractions` owns provider-neutral resolution and vault contracts. Secret value objects redact `ToString()` and clear their owned buffers when disposed.
- The Management Plane exposes write-only value operations. Resource reads and lists expose only value status.
- The local provider stores versioned AES-256-GCM payloads separately from control-plane documents. Every write uses a fresh nonce and authenticated tenant, workspace, vault, key, and format context.
- The Base64-encoded 32-byte master key comes from `AGENTSTRATION_MASTER_KEY_FILE`, preferred, or `AGENTSTRATION_MASTER_KEY` for development. An administrator may initialize a missing local key from the Vault Console. Generation happens on the server, uses a cryptographic random generator, creates the file atomically without overwrite, and returns only its path to the browser. No key is hardcoded or revealed by the Management Plane.
- Runtime resolution is late-bound through `ISecretResolver` and verifies tenant and workspace context.
- Model Provider declarations may reference a Secret. Credential forwarding across AEP requires a separate versioned protocol decision.

## Consequences

Agentstration can store and resolve local secrets offline without exposing values through its resource model. A missing master key does not prevent startup; an administrator can provide it through platform configuration or initialize it once from the Console. Losing this file makes existing local secrets unrecoverable, so operators must back it up independently. Environment and external/AEP vault providers, rotation, leasing, reveal operations, certificates, and cross-workspace sharing remain outside V1.
