# ADR-0018: Persisted model-provider declarations and dynamic clients

## Status

Accepted — 2026-08-03

## Context

ADR-0015 deliberately exposed providers as read-only configuration projections. That prevents an operator from declaring a native Ollama installation, changing its URL, or managing more than the provider wired at host startup. A startup-bound singleton Ollama client would also keep using the old URL after a Management update.

## Decision

- `ModelProviderResource` is a canonical Management Plane resource stored in the existing SQLite control-plane document store.
- The durable declaration contains identity, display name, provider type, absolute endpoint URL, management mode, and provider-keyed adapter options. Credentials are not accepted in the URL or provider options.
- Create and update validate the registered adapter, HTTP(S) endpoint shape, and native provider options, but do not require the endpoint to be online.
- Health, connectivity tests, and installed models are observed dynamically and are not persisted as desired state.
- Model profiles reference the provider's full resource ID. Provider deletion is rejected while any profile retains that exact reference.
- REST and Blazor expose create, read, update, delete, usages, test, and model-discovery operations. Canonical writes and deletes use ETags.
- Runtime resolution reads the current provider resource and the Ollama adapter creates an endpoint-specific client from it. No singleton Ollama endpoint is captured at process startup.
- Aspire remains responsible only for optional Ollama provisioning and its initial seed URL. After the first seed, SQLite is authoritative.
- V1 does not preserve the previous configuration-backed provider shape. Development databases may be reset and reseeded.

## Consequences

An Ollama URL edited in the console is used by subsequent runtime resolutions without restarting Agentstration. Offline provider declarations remain valid, while their observed state reports unavailable. Discovered models cannot drift into desired state, and an in-use provider cannot be orphaned accidentally. Adding credentials or another provider type requires an explicit connection/secret boundary and adapter registration.

This ADR supersedes ADR-0014 and ADR-0015 only where they describe providers as configuration-backed or read-only; their profile-resolution and dynamic-discovery decisions remain accepted.
