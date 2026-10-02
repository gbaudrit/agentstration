# ADR-0156: Crawl4AI web acquisition is an AEP Tool Provider

## Status

Accepted

## Context

Knowledge ingestion needs optional web acquisition without coupling the Knowledge domain, Flow runtime, or Agentstration process to one crawler. Crawl4AI is independently deployed, can change its native request contract, and may reach untrusted destinations. Returning complete documents through Flow state or a model-visible Tool result would also make payload size and data exposure difficult to govern.

The existing AEP boundary can announce MCP Tools, while Agentstration keeps Tool assignment, approval, execution hooks, and audit as the authorization boundary. The Artifacts vertical already defines governed chunked staging and Storage Flow composition, but an external AEP process cannot safely access an in-process filesystem path.

## Decision

Ship Crawl4AI integration as an optional autonomous AEP extension. It has no dependency on Knowledge contracts. AEP announces five MCP Tool contributions: single-page fetch, bounded breadth-first crawl, text extraction, chunked temporary-content read, and idempotent temporary-content deletion.

The extension sends only validated URLs to a self-hosted Crawl4AI 0.9.0 or newer `/crawl` endpoint. It does not forward caller-supplied browser configuration, scripts, proxies, headers, cookies, or provider-native extraction settings. Agentstration configuration supplies an explicit domain allowlist, allowed ports, timeouts, page/depth/link limits, response/content bounds, and a file-backed API token. Destination and returned final URLs are checked against the allowlist and unsafe address classes; private ranges remain opt-in, while loopback, link-local, multicast, unspecified, documentation, benchmark, and reserved destinations remain blocked.

Acquisition content is written to a bounded extension-local temporary spool. Tool results contain opaque references, media type, length, digest, links, bounded metadata, source URL, and correlation ID rather than full documents. `content.read` transfers bounded base64 chunks to the governed StagedArtifact `create`/`write`/`seal` path selected by a Flow. `content.delete` removes the extension-local copy after transfer; retention expiry is the recovery path for abandoned copies. The temporary reference is not a `StagedArtifact` and is not durable.

The extension implements multi-page traversal itself with deterministic breadth-first semantics instead of exposing Crawl4AI deep-crawl configuration. Aspire starts the extension only when explicitly enabled. Its default managed mode also starts a pinned, least-privilege Crawl4AI container and shares a generated token through a protected host file mounted read-only into the container. An external mode accepts an operator-managed endpoint and token file. The default Agentstration topology remains offline and unchanged.

## Consequences

- Web acquisition remains replaceable Flow composition, not a Knowledge domain dependency.
- Every acquisition and transfer operation remains a separately governable Tool.
- Large document bodies do not enter normal Flow transition state or model context merely because acquisition completed.
- Operators must explicitly approve reachable domains. They may use Aspire-managed Crawl4AI for development or operate a compatible external service.
- Private intranet acquisition requires an explicit operator opt-in and still cannot target local metadata, loopback, link-local, or reserved ranges.
- Temporary references are meaningful only to the issuing extension instance; Flows must stage required content before expiry or fail and retry acquisition.
