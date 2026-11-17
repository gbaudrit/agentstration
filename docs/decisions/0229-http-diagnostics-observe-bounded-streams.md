# ADR-0229: General HTTP diagnostics observe bounded streams at host boundaries

## Status

Accepted

## Context

[#1148](https://github.com/gbaudrit/agentstration/issues/1148) requires configurable HTTP request and optional response-content diagnostics beyond agent calls. Host HTTP spans already exist. The GenAI payload handler is model-provider-owned and buffers complete responses, so applying it globally would change streaming behavior and introduce provider dependencies into independent hosts.

## Decision

Add a small host infrastructure assembly, `Agentstration.Http.Diagnostics`, depending only on the ASP.NET Core shared framework. Web/API, Console, Workplace and Runtime Worker explicitly register its configuration, middleware and global `IHttpClientFactory` handler. Existing host OpenTelemetry composition subscribes to its activity source; diagnostics do not require an exporter. Aspire forwards explicitly configured AppHost options to its participating application processes.

`Observability:Http` controls metadata, request and response bodies independently. Body capture is Development-only, disabled by default, bounded by bytes and filtered by path/destination. Capture tees request/response streams and the server's existing response `PipeWriter` without reading ahead, replacing the primary transport handler, or buffering a complete response. Unsupported, oversized, incomplete and invalid JSON content is omitted; only complete bounded JSON is recursively redacted and emitted. Sensitive identity/Secret routes suppress bodies. Headers, URI queries/user information and exception messages are not recorded.

Incoming content is attached to an exchange span. Since outgoing `ResponseHeadersRead` returns before body consumption, outgoing response capture emits a separate child payload span at EOF/disposal, retaining the exchange's parent context. It does not hold an exchange span open indefinitely or consume the response early.

For a matching outgoing call, a request-local options marker gives general diagnostics ownership over the legacy GenAI handler, avoiding duplicate capture and its response buffering. The marker is shared by its technical name to keep the general infrastructure independent of model providers. GenAI-only behavior remains available otherwise.

## Consequences

- All four hosts use the same capture/filter/redaction implementation without business or protocol dependencies.
- Standard logs work without a collector; Aspire payload visibility uses activity events and correlated structured logs.
- Metadata can run outside Development, while body capture requires an explicit Development configuration.
- SSE, binary, encoded, oversized or incomplete bodies are omitted, with no partial unredacted payload export.
- Outgoing response events appear only when the caller consumes or disposes the content; their duration is distinct from exchange time to headers.
- Manually constructed clients, synchronous sends, extension-native calls and arbitrary sensitive text detection remain outside this integration.
- No database, AEP/AWP wire contract, product UX or deployment topology changes are introduced.
