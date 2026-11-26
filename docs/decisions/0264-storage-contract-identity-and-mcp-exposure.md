# ADR-0264: Storage contract identity is independent of its MCP exposure

## Status

Accepted

## Context

Issue [#1377](https://github.com/gbaudrit/agentstration/issues/1377) adopts
Agentstration Storage Contract (ASC) for the existing unreleased storage SDK.
MCP already owns operational invocation; AEP owns discovery and protected
configuration. Calling the functional storage contract a protocol obscured that
boundary. The maintainer confirmed that no released compatibility surface exists.

## Decision

Replace the unpublished ASP names and wire identities in one coordinated change,
without aliases or dual readers. The autonomous SDK lives in `asc/`, with
`Agentstration.Asc.*` packages and `Asc*` public symbols. `AscContract` owns the
standalone contract constants and strict JSON options.

Each AEP Storage Type version declares an `asc` mapping with the exact contract
identity `agentstration.storage.contracts.mcp.v1` (display `ASC.mcp.v1`), revision
`1.0`, MCP protocol version `2026-07-28`, operation profile, MCP server, Tool
prefix, provider pins and protected binding path. The AEP validator accepts only
the implemented revision and exposure; it rejects a different major, unknown
revision, missing declaration or undeclared MCP server. Typed clients also check
the negotiated MCP version and canonical executable Tool schemas.

The identity major is a compatibility boundary, the revision identifies exact
contract semantics, and MCP protocol, provider software and NuGet package versions
are independent. The common identity does not grant capabilities: content and
document profiles remain separate, and query languages retain independent explicit
identities. AEP capability versions remain AEP discovery/configuration identities.

This supersedes only the ASP naming and unpublished wire identities of ADR-0192,
ADR-0193, ADR-0200, ADR-0205 and ADR-0220. Their authorization, logical target,
conditional write, session, uncertainty and provider-neutrality decisions remain
the behavioral baseline. Historical ADR bodies and release notes retain their
original terminology.

## Consequences

Providers, consumers, schema fixtures, protected binding headers and authored
configuration must be upgraded together. Start from a reset development state;
no migration of existing ASP state is provided.
There is no REST exposure, generic contract registry or new storage operation.

The [identity mapping and development procedure](../reference/asc-identity-migration.md)
records the replacement inventory. Retained receipts and operational data are not
rewritten or deleted by this change.
