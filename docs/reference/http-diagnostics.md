# HTTP diagnostics

General HTTP diagnostics cover incoming ASP.NET Core requests and outgoing **asynchronous** `IHttpClientFactory` clients in the authoritative Web/API host, separated Console, Workplace and Microsoft Agent Framework Runtime Worker. They are independent of agent/model instrumentation and work for HTTP and HTTPS after application-level TLS processing.

## Configuration

Add the following to the participating process's `appsettings.Development.json`, or configure equivalent environment variables:

```json
{
  "Observability": {
    "Http": {
      "Enabled": true,
      "LogMetadata": true,
      "MaximumBodyBytes": 16384,
      "IncludedPaths": ["/api/workspaces"],
      "ExcludedPaths": ["/_blazor", "/hubs"],
      "IncludedHosts": [],
      "ExcludedHosts": []
    }
  }
}
```

| Setting | Default | Behavior |
| --- | --- | --- |
| `Enabled` | `false` | Enables general diagnostics; existing HTTP/OpenTelemetry instrumentation remains independent. |
| `LogMetadata` | `true` | Logs method, sanitized target, status, elapsed milliseconds, error type and trace/span IDs. |
| `Categories` | Empty in shared registration; all `false` in AppHost | Boolean switches. Enabled traffic always captures both request and response bodies. |
| `MaximumBodyBytes` | `16384` | Per-body capture and exported-value limit; valid range 256–1048576 bytes. |
| `IncludedPaths` | `[]` | Empty means all paths; otherwise include matching path prefixes. |
| `ExcludedPaths` | `["/_blazor", "/hubs"]` in application settings | Excluded prefixes take precedence over included paths. The reusable registration has no implicit path exclusions. |
| `IncludedHosts` / `ExcludedHosts` | `[]` | Outgoing calls only; case-insensitive exact hosts, without scheme, port or wildcard. Exclusions take precedence. |

Paths match complete segments: `/api/workspaces` includes its descendants, but not `/api/workspaces-other`. Matching ignores case. To replace array defaults with an empty collection through configuration, note that .NET providers merge arrays by index; use an explicit configuration provider appropriate to the deployment or supply replacement entries. Options are validated at startup and changes require a process restart. Diagnostics can only be enabled in `Development`, because request and response body capture is always active for enabled traffic.

For a direct Web launch:

```bash
Observability__Http__Enabled=true \
dotnet run --project src/Agentstration.Web --launch-profile http-NoBootstrap
```

Configure each process that owns the boundary you need to inspect. Web observes incoming API calls; Console/Workplace observe their incoming requests and outgoing API clients; the Runtime Worker observes its outgoing authority clients. An exchange can therefore appear on both the caller and server resources. Manually created `HttpClient` instances, synchronous `Send` calls, browser network traffic and extension-to-provider HTTP calls are outside this integration. Extension-host instrumentation remains separate (#457).

For Aspire, the section is present and disabled in **`src/Agentstration.AppHost/appsettings.json`**; it can be overridden in `appsettings.Development.json`. Explicitly configured `Observability:Http` values are forwarded to the Web resource (`agentstration-console`), Workplace and every Runtime Worker. The committed AppHost defaults deliberately disable diagnostics in its children until the operator enables the master switch and selected categories. The separated Console is configured in its own host when launched independently. This adds no collector or external-service requirement to standalone startup.

## Categories and AppHost defaults

The AppHost's committed `appsettings.json` includes the full block with `Enabled: false` and every category set to `false`. The AppHost forwards this configuration to its participating processes. To inspect only health responses in Development, change it to:

```json
{
  "Observability": {
    "Http": {
      "Enabled": true,
      "Categories": {
        "Health": true,
        "Api": false,
        "Awp": false,
        "Aep": false,
        "Bff": false,
        "Other": false
      }
    }
  }
}
```

| Category | Matching paths, in precedence order |
| --- | --- |
| `Health` | `/health` and its descendants, including `/health/live` and `/health/ready` |
| `Awp` | `/api/awp` and its descendants |
| `Aep` | `/aep`, `/api/aep` and their descendants; `/.well-known/aep` |
| `Bff` | `/bff` and its descendants |
| `Api` | Remaining `/api` routes |
| `Other` | Remaining routes |

Each category is a boolean: `true` enables request **and** response body capture; `false` disables the category. There is no capture-level enum or separate request/response switch. Omitted categories inherit the master switch. `Enabled` remains the master switch, and path/host exclusions and sensitive-route protection always take precedence. Invalid category names and non-boolean values fail startup. The committed AppHost lists every category as `false`, so enabling the master switch alone does not activate one.

The same policy applies to incoming and outgoing traffic. `agentstration.http.category` is added to diagnostic spans (including deferred response spans); structured logs carry a `Category` field. `false` suppresses this feature's additional diagnostics, while the pre-existing standard HTTP spans remain independent. GenAI's non-HTTP instrumentation remains controlled by `Observability:GenAI`.

`/health` is no longer excluded by application defaults. A `GET /health` normally has no request body; capture reports `[empty body]` instead of inventing a payload. The Web health response is JSON. Standard ASP.NET Core health responses are text: exactly `Healthy`, `Degraded` and `Unhealthy` are supported on `/health` and its descendants. Other text content remains omitted. Open a diagnostic span's payload events to see the body; the trace list itself displays span names, not event content.

## Logs and Aspire traces

The logger category prefix is `Agentstration.Http.Diagnostics`, at `Information` level. Structured logs work without Aspire or an OTLP endpoint. Under Aspire, the existing OTLP configuration exports the additional activity source `Agentstration.Http.Diagnostics`.

In **Traces**, open the exchange and inspect the `http.incoming.diagnostics` or `http.outgoing.diagnostics` span. Request and incoming response content appears in `http.request.payload` / `http.response.payload` events, under `agentstration.http.payload.body`, with content type and truncation metadata. An outgoing response consumed after headers arrive produces a **child `http.response.payload` span** once reading reaches EOF or the content is disposed. It retains the original exchange's trace and parent span IDs even when consumption occurs in a different context. The outgoing exchange duration measures time to response headers; the payload child is emitted at consumption time.

Aspire versions may expose events in a separate span-detail area. The matching structured payload log includes explicit `TraceId` and `SpanId` fields for lookup in **Structured logs** if the dashboard does not render event attributes inline. Payload visibility still depends on trace sampling, collector limits and retention. The implementation emits OpenTelemetry activity events; dashboard rendering must be smoke-tested against the installed Aspire version.

## Capture boundaries and sensitive content

- Bodies remain disabled by default. Opt-in capture can contain user content even after credential redaction; use narrowly selected diagnostic routes and treat telemetry storage accordingly.
- Complete, valid `application/json` and `+json` bodies are emitted, along with the three standard plain-text health status values described above. Unsupported media types (including binary, form data and SSE), content-encoded bodies and invalid JSON are omitted. Responses still reach callers unchanged.
- Common credential properties are recursively redacted, including `api_key`, `access_token`, `password`, `client_secret`, `secretCapability`, `secretValueBase64`, cookies, pairing codes and private keys. This is a field-name rule, not a detector of arbitrary sensitive text.
- Account, identity, authentication, login/logout, bootstrap, pairing/enrollment, Secret/credential and redemption route segments suppress **all body capture**, even when explicitly included. Their diagnostic target is replaced with `/[sensitive route]`. Add exclusions for any other application-specific sensitive routes or destinations.
- This feature never records HTTP headers, query strings, URI user information or exception messages. Ordinary path values remain visible; exclude routes whose path segments contain sensitive values. Existing framework/client logs and standard HTTP spans have their own configuration and redaction behavior.
- Capture observes streams without reading ahead or buffering entire responses. Once a body exceeds the limit, the retained bytes are discarded and the event reports `Truncated=true` with an omission reason. Raw partial JSON is never exported, because it cannot be reliably redacted. The transport continues normally.
- Unread/partially read response streams, seeked streams and send-file output produce omission markers. Capture cannot force a caller or endpoint to consume a body. Outgoing content that is never consumed or disposed does not produce a completed payload event.
- For matching outgoing calls, general HTTP diagnostics take precedence over `Observability:GenAI:HttpPayloadCapture` using a request-local marker. The GenAI handler then passes through, including when only general metadata logging is selected. Existing GenAI-only behavior is unchanged for calls outside the general filters or when general diagnostics are disabled. This avoids duplicate capture and the legacy response buffering on exchanges owned by general diagnostics.

See [ADR-0193](../decisions/0193-http-diagnostics-observe-bounded-streams.md) for stream ownership, [ADR-0194](../decisions/0194-http-diagnostics-use-category-policies.md) for categories and health status capture, and [ADR-0195](../decisions/0195-http-categories-enable-both-bodies.md) for the current boolean controls.
