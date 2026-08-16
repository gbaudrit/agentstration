# ADR-0040: Console API calls propagate only an explicitly trusted Web session

Status: Accepted — 2026-08-16

## Context

The Agentstration Console uses Interactive Server rendering. Its typed API clients therefore execute on the server rather than in the browser. A browser request can be authenticated by the ASP.NET Core Identity/OIDC application cookie while a subsequent server-side `HttpClient` call has no authentication unless the Console deliberately propagates it. Calling the protected APIs anonymously produces `401`, while inventing an internal administrator header or bypassing authorization would create a second, unsafe security model.

Some API base addresses can later point at another process or host. Forwarding the browser cookie indiscriminately would disclose a bearer credential to an untrusted destination.

## Decision

Console API endpoints have an explicit `ForwardSessionCookie` option, disabled by default in the options model and enabled in the shipped standalone configuration only for the APIs hosted by the same Agentstration instance.

When enabled, a delegating handler:

- forwards only the named Agentstration application cookie, including its ASP.NET Core chunk cookies;
- forwards only for requests whose scheme, host, and port exactly match the configured API base address;
- adds the already resolved Workspace identifier as a context-selection header;
- never forwards unrelated browser cookies;
- never follows redirects at the underlying HTTP handler;
- does nothing for an anonymous request or when no application cookie is present.

The receiving API still authenticates the cookie through the standard ASP.NET Core handler, resolves the Principal, validates Workspace membership, and evaluates its normal policy. The Workspace header selects context but grants no permission. There is no trusted-user, trusted-role, or administrator bypass.

In OIDC mode, API requests carrying an explicit Bearer token continue to select JWT bearer authentication. Same-instance Console calls carrying the application cookie select cookie authentication. API clients outside the Console continue to use standard Bearer access tokens; this decision does not create an Agentstration token format or authorization server.

## Consequences

- Local and OIDC Web sessions can use the server-side Console clients without losing their Principal or selected Workspace.
- A configured remote API receives no Web credential unless an operator explicitly enables forwarding for that exact origin and arranges compatible cookie protection. Bearer token acquisition/propagation is the preferred future boundary for separately deployed APIs.
- The Console remains a thin server-side Web client. Business authorization stays in API policies and handlers.
- Session forwarding is testable independently from the API clients, including origin isolation and chunked cookies.

## Follow-up

When an API is deployed independently, replace cookie sharing with OAuth access-token acquisition and forwarding for the intended audience. Add persistent, shared Data Protection only if multiple instances of the same Web application need to accept the same interactive session.
