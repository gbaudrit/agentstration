# ADR-0259: Preview routes use owned tunnels and an isolated Access proxy

Status: Proposed — 2026-11-25

## Context

[#1285](https://github.com/gbaudrit/agentstration/issues/1285) exposes the existing
Azure preview vertical over HTTPS to a dedicated Entra group and CI service
token. Selected PR images are application code, not trusted deployment tooling.
Neither a shared Access credential nor a management token may reach that code.
The Runtime must remain unaware of infrastructure, and teardown must recover
from interrupted API writes without adopting foreign resources.

## Decision

Keep the provider-neutral request and internal `pr-N` identity. Map it externally
to `dev-pr-N.agentstration.cloud`. Use the existing Azure ownership blob and its
exclusive lease for per-preview Cloudflare tunnel and proxied CNAME references.
Persist an owner-derived creation name before creating a tunnel; DNS also carries
the random owner in its comment. Reconcile interrupted creations only against
that intent and exact resource identity/configuration. Refuse foreign resources.
Keep the shared Access application and its policies read-only; audit their Entra
group, single identity provider, dedicated Service Auth rule and overlap before
creating/configuring routes and before publishing a URL.

Run digest-pinned `cloudflared` and Caddy infrastructure containers outside the
application Compose project. Both use host networking, closed loopback listeners,
read-only filesystems, no capabilities, no Docker socket and no persistent logs.
The connector validates Access JWTs against the audited application's AUD, uses
one exact-host ingress and a terminal 404 rule. It receives only its own tunnel
token through an ARM protected parameter and a root-only mounted file. Caddy is
a local HTTP/WebSocket proxy; it does not terminate public TLS or request ACME
certificates. Strip all `Cf-Access-*` headers and only the `CF_Authorization`
cookie before forwarding. Preserve application authentication and normalize the
public Host and HTTPS forwarding headers. Enable ASP.NET's existing forwarded
headers support for this guest route; no product deployment logic is added.

Management and CI probe credentials remain in the protected runner. Never follow
probe redirects with credentials. Publish the HTTPS URL only after the exact
application digests and connector are healthy, observed Azure NIC/NSG deny all
inbound ports, guest application binding is loopback-only, anonymous/invalid-token
probes are denied, and the dedicated CI token reaches readiness over HTTPS.
Status repeats protection checks before reporting a URL. Failed deployment
withdraws its owned DNS route and leaves resumable ownership state. Destroy
withdraws DNS, stops/deallocates or deletes the VM, then removes owned tunnel
sessions and the tunnel. Preserve keeps application data and its identity;
explicit reset removes it. Neither operation alters the shared Access policy.

## Consequences

HTTPS termination and access enforcement depend on Cloudflare for this optional
deployment adapter, without making Azure or Cloudflare structural dependencies of
Agentstration or its OCI packages. Caddy adds one small infrastructure image to
avoid implementing a custom WebSocket reverse proxy or granting DNS-zone rule
editing rights. Connector tokens are scoped to one tunnel but remain sensitive;
host root and deployment operators remain trusted. DNS caches can outlive record
deletion, so origin JWT enforcement and stopping the connector remain essential.

The first PR delivers offline contracts and a real pinned-image transport smoke.
Cloudflare/Azure write qualification, authorized/unauthorized human login and the
deployed browser journey remain explicit post-merge acceptance work. #1284 and
#1285 stay open until that evidence exists. Automatic PR/TTL cleanup stays #1286;
this change does not introduce a background reconciler or a second inventory.
