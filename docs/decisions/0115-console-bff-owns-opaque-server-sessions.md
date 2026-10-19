# ADR-0115 — The Console BFF owns opaque server-side sessions

## Status

Accepted

## Context

ADR-0111 created the independently hosted operations Console and ADR-0112 gave that process a bounded workload identity. Sharing the authoritative server's application cookie or Data Protection keys would still couple the two hosts and would expose an API credential to the browser. The Console instead needs an interactive session that can represent a validated human identity without becoming a second identity authority.

## Decision

The Console BFF issues its own secure, HTTP-only, SameSite=Lax cookie. The cookie contains only the protected opaque key produced by ASP.NET Core's `ITicketStore` integration; the authentication ticket, Principal, authentication method, provider, selected Tenant and selected Workspace remain in an `IBffServerSessionStore` owned by the BFF.

The default implementation is bounded in-process memory for the local single-replica profile. The interface is the explicit replacement boundary for a shared store when the Console is replicated. Sessions have both an idle timeout and an absolute lifetime. Persistent browser sessions use a separately bounded absolute lifetime, not an unbounded refresh token.

Local credentials are posted from the browser only to the Console. The Console authenticates its private request with the `console-bff` workload credential, and the authoritative identity API verifies the password without issuing or sharing its own application cookie. It returns only the active human Principal and an authorized current Tenant/Workspace context. The BFF revalidates that Principal and context against current authoritative state on every authenticated browser request. Failure or loss of authority fails closed.

Login replaces any prior BFF session key, logout removes the server-side ticket, and return URLs must remain local to the Console origin. External OIDC login will use the same session model after the governed provider journey in #204 is available; this decision does not accept browser-asserted issuer or subject values.

This supersedes ADR-0043 only for the separated Console host. The all-in-one `Agentstration.Web` profile continues to use its same-origin application session.

## Consequences

The browser never receives an authoritative API credential or identity claims that the BFF can use after the corresponding server record is removed. Principal disablement, membership removal, Workspace disablement, expiry, logout and authority outages invalidate access on the next request. The local store requires no external service, but a replicated Console deployment must provide a shared `IBffServerSessionStore` before it can advertise session continuity across replicas.

Downstream business API authorization is not solved by this session. A later increment exchanges the revalidated BFF session for short-lived, audience-bound delegation as defined by #212.
