# ADR-0159 — Knowledge acquisition snapshots governed source configuration

## Status

Accepted

## Context

A published ingestion Flow needs source-specific values such as seed locations, crawl bounds, or provider-neutral routing keys. Requiring an acquisition caller to submit those values would let each request replace administration-owned policy and would couple the acquisition API to concrete providers. Resolving the mutable Knowledge Source again during recovery could instead run the same acquisition with a different configuration.

Tool selection has a separate governance boundary. Published Flows pin Tool or ToolSet versions and their declared routes; a JSON value stored on a Knowledge Source must not grant access to another Tool or ToolSet.

## Decision

`KnowledgeSource` owns a bounded `acquisitionConfiguration` JSON object. It is ordinary governed resource state: Workspace-scoped writes require the existing resource administration permission, the value is limited to 64 KiB, and credentials must be represented by references rather than embedded secrets.

Starting a Knowledge acquisition resolves the current source once. Agentstration records the source UID, generation, and an immutable copy of its acquisition configuration on the `KnowledgeAcquisition`, then supplies those values in the trusted `knowledge.ingestion/v1` Flow input as `knowledgeSourceUid`, `knowledgeSourceGeneration`, and `sourceConfiguration`. Caller-supplied `parameters` remain a separate bounded object and cannot replace the source configuration. Recovery reconstructs the Flow input from the acquisition snapshot rather than from the current mutable source.

The source configuration is data, not authority. A Flow may use it in mappings, conditions, or routers, but every executable Tool and ToolSet remains explicitly pinned by the published Flow definition. A common ingestion Flow can therefore serve multiple similarly configured sources without enabling dynamic selection of ungoverned providers.

Bounded content readers expose an explicit next offset so a provider-neutral repeat step can carry progress between child Flow runs without decoding provider payloads inside the Flow engine.

## Consequences

- Acquisition callers provide only request-scoped parameters; administration-owned source configuration is resolved server-side.
- Recovery and audit retain the exact configuration that started a run even after the Knowledge Source changes.
- Shared ingestion Flows can branch on source configuration while their Tool authority remains statically reviewable.
- Source configuration is visible wherever the Knowledge Source or acquisition resource is readable and therefore must not contain secret material.
- Existing `knowledge.ingestion/v1` Flow schemas must be republished with the additional required source identity and configuration fields before they can be activated.
