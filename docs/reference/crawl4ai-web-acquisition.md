# Crawl4AI web acquisition

Agentstration provides an optional AEP extension that exposes a self-hosted Crawl4AI service as governed Tools. It is an acquisition provider for Flow composition; it does not create or own Knowledge Sources and is not required by the executable default.

## Requirements and activation

Run [Crawl4AI](https://github.com/unclecode/crawl4ai) 0.9.0 or newer and enable its API authentication. This baseline retains Crawl4AI's [secure request boundary and SSRF protections](https://docs.crawl4ai.com/core/security/).

Aspire uses managed provisioning by default when the extension is enabled. It starts the pinned Crawl4AI container, creates a worktree-local protected token file, mounts that file read-only into the container, passes the same file to the AEP extension, and waits for Crawl4AI readiness before starting the extension:

```text
Crawl4AI__Enabled=true
Crawl4AI__Provisioning=Managed
Crawl4AI__AllowedDomains__0=docs.example.com
```

The managed image defaults to `unclecode/crawl4ai:0.9.4`; `Crawl4AI:Image` and `Crawl4AI:ImageTag` allow an explicit operator override. The container is not created while `Enabled` is false, so Docker remains optional for the default topology.

Use external provisioning when Crawl4AI is administered independently. The endpoint is then required, and an optional protected token file can be supplied to the extension:

```text
Crawl4AI__Enabled=true
Crawl4AI__Provisioning=External
Crawl4AI__Endpoint=https://crawler.example.net
Crawl4AI__ApiTokenFile=C:\protected\crawl4ai.token
Crawl4AI__AllowedDomains__0=docs.example.com
```

`Enabled` and `Provisioning` are consumed by AppHost. The acquisition limits and destination policy are passed to the extension. A manually launched extension uses the extension settings directly without those two AppHost controls.

The extension sends a minimal `{ "urls": ["…"] }` request to Crawl4AI. Flow or Agent input cannot set browser arguments, execute JavaScript, supply cookies or headers, choose a proxy, or override the configured endpoint.

## Governed Tool contributions

| Contribution | MCP Tool | Purpose |
| --- | --- | --- |
| `web.fetch` | `web_fetch` | Acquire one page. |
| `web.crawl` | `web_crawl` | Acquire a breadth-first page set within configured depth and count limits. |
| `content.extract` | `content_extract` | Produce normalized text from previously acquired HTML or text. |
| `content.read` | `content_read` | Read one bounded base64 chunk from temporary content. |
| `content.delete` | `content_delete` | Idempotently remove temporary content. |

The AEP registration materializes these as ordinary governed Tools. Administrators still enable the provider and Tools, group the exact published Tools in a ToolSet when desired, and assign only the required unit Tools to an Agent or Flow.

The MCP schemas expose these stable arguments and results:

| Tool | Inputs | Result |
| --- | --- | --- |
| `web_fetch` | `url`; optional `correlationId` | `correlationId` and one content reference. |
| `web_crawl` | `startUrl`; optional `maximumDepth`, `maximumPages`, `correlationId` | Applied bounds, ordered content references, and `truncated`. |
| `content_extract` | `contentReference`; optional `correlationId` | A separate normalized `text/plain` content reference; source URL and media type are resolved from the opaque reference. |
| `content_read` | `contentReference`, `offset`; optional `maximumBytes` | Base64 chunk, original offset, and `endOfContent`. |
| `content_delete` | `contentReference` | `true` after the idempotent deletion attempt. |

A content reference contains `reference`, `sourceUrl`, `mediaType`, `length`, `sha256`, `links`, and scalar `metadata`. Tool-specific optional values remain optional in the generated JSON Schema; configured ceilings always take precedence over smaller caller-requested crawl and read bounds.

Fetch and crawl results never embed the acquired document. Each result returns an opaque reference with its source URL, media type, byte length, lowercase SHA-256 digest, accepted links, bounded scalar metadata, and correlation ID. A reference begins with `crawl4ai-content:` but its remaining value is opaque.

`content_read` accepts the reference, a zero-based byte offset, and an optional bounded byte count. It returns the same offset, base64 content, and an end-of-content flag. Callers advance the offset by the decoded byte count.

## Ingestion Flow composition

A typical ingestion Flow is:

```text
Knowledge Source identifier and acquisition parameters
  -> route to the selected acquisition Tool or ToolSet
  -> web_fetch or web_crawl
  -> optional content_extract
  -> StagedArtifact create
  -> repeat content_read -> StagedArtifact write
  -> StagedArtifact seal
  -> content_delete
  -> parsing, chunking, indexing, or a Storage Flow
```

The temporary Crawl4AI reference is not an Agentstration `StagedArtifact`. It is intentionally local to the extension and expires. A Flow must transfer content through the governed staging Tools before handing it to a remote Agent, another Flow, or durable storage. On retry, `content_delete` is safe to repeat; an expired reference requires acquisition to restart.

## Limits and destination policy

| Setting | Default | Constraint |
| --- | ---: | --- |
| `AllowedDomains` | none | Required; exact domains and their subdomains are accepted. |
| `AllowedPorts` | `80`, `443` | Every requested and returned URL must use an allowed port. |
| `AllowedMediaTypes` | `text/html`, `text/markdown` | Accepted normalized content variants returned by Crawl4AI. |
| `AllowPrivateAddresses` | `false` | Enables approved RFC1918, shared-address, or IPv6 ULA targets; unsafe special ranges remain blocked. |
| `MaximumDepth` | `3` | Operator ceiling, from 0 through 10. |
| `MaximumPages` | `25` | Operator ceiling, from 1 through 500. |
| `RequestTimeoutSeconds` | `60` | Per Crawl4AI request, from 1 through 600 seconds. |
| `MaximumResponseBytes` | 8 MiB | Maximum upstream JSON response. |
| `MaximumContentBytes` | 4 MiB | Maximum UTF-8 content for one acquired page. |
| `MaximumLinksPerPage` | `250` | Maximum link candidates processed per page. |
| `MaximumReadChunkBytes` | 64 KiB | Maximum decoded content returned by one read. |
| `ContentRetentionMinutes` | `60` | Sliding temporary-content retention, at most seven days. |
| `MaximumSpoolBytes` | 256 MiB | Total extension-local temporary content ceiling. |

Every DNS result must satisfy the destination policy. The final URL reported by Crawl4AI is checked again, so a redirect cannot escape the allowlist. Crawl4AI itself must also retain its secure request boundary; this extension does not weaken or replace the crawler's SSRF controls.

`/health` reports extension liveness. `/health/ready` additionally probes the configured Crawl4AI service and returns `503` while it is unavailable. Acquisition errors expose stable `crawl4ai_*` codes and bounded diagnostics without returning credentials or complete upstream responses.
