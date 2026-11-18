# ADR-0225: Structured storage does not define agent Memory policy

## Status

Proposed

## Context

[#1156](https://github.com/gbaudrit/agentstration/issues/1156) is explicitly on hold. Agentstration already distinguishes agent memory policies from storage providers. The active document vertical must not introduce a competing memory lifecycle.

## Decision

The active delivery exposes governed generic JSON document Tools that agents and Flows may explicitly use for observations, diagnostics and shared state. These are Storage operations, without automatic memory creation, retrieval injection, retention, cross-agent sharing, expiration or deletion policy.

A future Memory integration must compose existing Memory ownership and policy mechanisms with collection isolation and least-privilege Tools. Storage capability alone grants no memory access. Vector indexing, embeddings and Search are optional future capabilities. No Memory provider or default policy is added by #1150 while #1156 remains on hold.

## Consequences

The same document capability supports universal application data today and can become a physical Memory backend later. Resuming #1156 requires explicit maintainer scope and tests for retention, deletion, sharing and runtime integration. It does not gate the active V1–V3 implementation.
