# ADR-0116 — The Console BFF delegates short-lived API requests

## Status

Accepted

## Context

ADR-0115 gives the separated Console an opaque browser session and ADR-0112 gives its process a workload identity. The workload credential must authenticate only private identity operations; it must not authorize business APIs. The browser session cannot be sent to Management, Work, Flow, or Runtime APIs as an API credential.

## Decision

For each server-side API call, the Console checks that its local session is active and revalidates the human identity and selected Tenant/Workspace through the private identity authority. It requests a delegated token over the instance-bound, signed BFF workload channel. The authority repeats that validation, then signs a short-lived RS256 token for one of the Management, Work, Flow, or Runtime audiences. The token carries a Principal ID, Tenant ID, Workspace ID, opaque session ID, credential type, audience, issue and expiry times, and a random token ID. It carries no roles, permissions, provider claims or personal attributes. The configured lifetime is 30–300 seconds, with a 120-second default.

Only Console server-side HTTP clients attach the token, and only when the destination matches the configured API origin. Their handler sets the Authorization header per request; neither the browser cookie nor rendered HTML contains the token. Private issuance and public-key discovery require the BFF workload credential. Business routes reject that workload credential and accept delegation only for their assigned audience.

Every delegated API request verifies the signature, issuer, audience and expiry, then reloads the current Principal, Tenant, Workspace and access state. Existing route policies evaluate current permissions. Principal disablement or access removal takes effect on the next API request. A changed local `SecurityStamp` invalidates the BFF session on its next authority revalidation. BFF logout and session expiry prevent further token use by the BFF and evict its token cache. A copied token outside the BFF can remain cryptographically valid until expiry, so it must never be exposed to the browser or logs.

The authority stores an RSA private key in its data directory by default. Operators can specify `Agentstration:InternalDelegation:SigningKeyFile` and `PreviousPublicKeyFiles`. Rotation installs a new signing key and retains the previous public key for at least the maximum token lifetime, then removes it. Replicated authoritative API instances must use the same signing key and trusted public-key set. The local BFF session store remains in process; replicated Console instances need a shared implementation of `IBffServerSessionStore` before relying on cross-replica session continuity.

## Consequences

The Console can use existing typed clients and API authorization without sharing application cookies, BFF workload credentials, or server-side tickets with the browser. The authoritative API remains the source of truth for revocation and permissions. An authority outage fails delegated requests closed. This increment handles local sessions; external OIDC sessions depend on #204 and #211. `Agentstration.Web` keeps its existing same-origin authentication path.
