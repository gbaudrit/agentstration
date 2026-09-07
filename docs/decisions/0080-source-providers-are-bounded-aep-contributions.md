# ADR-0080 — Source Providers are bounded AEP contributions

## Status

Accepted

## Context

Sources need replaceable acquisition transports. A Source Channel may use Git or another distribution mechanism, but portable Source definitions must not embed an Agentstration installation's provider resource name or move catalog, Bootstrap, and Pack semantics into an extension. Acquiring mutable selectors also needs a stable boundary between discovering the current content and retaining the exact content used by Agentstration.

## Decision

- AEP registers the `aep.source-provider` capability and `source-provider` contribution kind. A contribution publishes an id and display metadata; its provider-specific Channel configuration is described by an immutable versioned option set with scope `source-channel`.
- A Source Provider has two operations. `Resolve` maps a configured selector to an immutable provider revision and integrity metadata. `Materialize` accepts that exact revision and returns an opaque archive with its revision, media type, SHA-256 digest, entry count, and expanded size.
- Materialization limits are explicit protocol input. The server intersects the requested limits with its configured maximum archive bytes, entry count, expanded bytes, and timeout. It validates the returned revision, limits, and digest before responding. The canonical client repeats revision, limit, and digest checks.
- Agentstration stores an ordinary `SourceProvider` resource containing a display name, a typed reference to the owning `ExtensionRegistration`, and the contribution id. The registration remains the only owner of the AEP endpoint, enabled state, extension identity, and credentials. Portable Source manifests bind logical names to these local resources separately.
- The archive remains opaque to AEP. Agentstration owns Source identity, SourceVersion and Channel lifecycle, path isolation, snapshot persistence, catalog parsing, Bootstrap semantics, and Pack semantics. Source acquisition is native AEP behavior and is not exposed as an MCP agent tool.

## Consequences

One extension can provide several acquisition mechanisms, and a Source can use different locally selected providers without hardcoding provider resource names. Resolve and Materialize make mutable remote selectors safe to pin before use. The JSON archive representation is simple and interoperable but incurs base64 overhead; strict compressed and expanded limits are therefore mandatory. Git behavior, Source persistence, provider binding selection, catalog interpretation, and scheduling remain separate increments.
