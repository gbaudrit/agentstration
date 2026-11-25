# Shared preview access

[#1285](https://github.com/gbaudrit/agentstration/issues/1285) protects manually
deployed previews through Cloudflare Tunnel and a shared Access policy. The
selected domain is `agentstration.cloud`; PR hostnames will be
`dev-pr-123.agentstration.cloud`. The existing `agentstration.io` website and
`docs.agentstration.io` DNS remain independent.

The read-only configuration audit is complemented by an owned route in the
existing [manual deployment](preview-on-demand.md). It publishes an HTTPS URL
only after protection checks pass. #1284 and #1285 remain open until the merged
route has been qualified against Azure, Cloudflare and real Entra users.
See [ADR-0259](../decisions/0259-preview-routes-use-owned-tunnels-and-an-isolated-access-proxy.md).

## One-time configuration

Use an active Cloudflare DNS zone and a Zero Trust organization. Set up Microsoft
Entra ID as the dedicated identity provider with **Support Groups** enabled.
Register a separate single-tenant Entra application using a Web callback:

```text
https://agentstration.cloudflareaccess.com/cdn-cgi/access/callback
```

Use the actual Zero Trust team domain if different. Enter the Entra client secret
directly in Cloudflare, retain its expiry in the operator's credential inventory,
and renew it before it expires. It is separate from the GitHub/Azure deployment
identity. Follow the [Cloudflare Entra integration guide](https://developers.cloudflare.com/cloudflare-one/integrations/identity-providers/entra-id/)
for the delegated Microsoft Graph permissions and administrator consent.

Create an Entra security group such as `Agentstration-Preview-Users` with assigned
membership, and add the people authorized to use previews. In Cloudflare Access,
create a **self-hosted** application with these settings:

| Setting | Value |
| --- | --- |
| Name | `Agentstration Development` |
| Public subdomain | `dev-*` |
| Domain | `agentstration.cloud` |
| Path | Empty, covering the whole application |
| Session duration | One hour recommended |
| Login methods | Only the dedicated Microsoft Entra identity provider |
| Instant authentication | Enabled for direct Microsoft login |

Configure exactly two policies in the initial audited setup:

1. **Allow**: Include one **Azure Groups** rule using the Entra group's **object
   ID** and the dedicated identity provider. An additional Require rule for that
   same login method is supported. Do not add alternate Include rules: they are
   OR conditions and could broaden access.
2. **Service Auth**: Include one specific service token, such as
   `agentstration-preview-ci`. Use the service token selector, not "any valid
   service token". No interactive login requirement belongs on this policy.

For a manually entered Azure Group, the audit accepts an omitted, null or empty
`identity_provider_id` only within the application's already-verified single
Entra provider scope. Legacy API representations can use `connection_id` for the
provider reference. Every non-empty value in either field must match the selected
provider; if both fields are returned, both are checked. Neither alias takes
precedence over a conflicting value. For a legacy schema example, see the
[Cloudflare Access group API client representation](https://hub.windmill.dev/scripts/cloudflare/6362/create-an-access-group-cloudflare).
Cloudflare can also include `name` display-label metadata in the Azure Group
selector, as observed in the [live configuration audit](https://github.com/gbaudrit/agentstration/actions/runs/45627527234).
The audit accepts a missing/null label or a string of at most 256 characters;
it never uses the label instead of the group object ID or provider checks, and
never exports its value. `humanGroupLabelPresent` records only field presence.
The specific CI service-token selector accepts the same bounded `name` metadata;
`token_id` remains mandatory and no other selector fields are accepted.
`automationTokenLabelPresent` records only label presence, never its value.
Other unknown selector fields remain an error. Diagnostic output includes only a fixed
set of known field names and the extra-field count, never keys outside that set,
group names, provider IDs or credential values. With either representation,
multiple login providers, other provider types and disabled group support remain
errors. The result records `humanProviderBinding` as `application-only` or
`application-and-rule`; `humanProviderFields` records the recognized alias names
present in the response without their values. This is configuration evidence, not a successful group
membership/authentication test.

If the picker reports **Failed to retrieve SCIM groups**, use the Entra group's
object ID as the manual value; a name alone is not accepted by the audit. SCIM
synchronization is not a prerequisite for this manual configuration. Check that
**Support Groups** is enabled and save the rule. If an audit still fails, its
diagnostic distinguishes an invalid group ID, unexpected selector fields, an
invalid provider ID and an explicit provider mismatch without logging values.

All other access is implicitly denied. The initial audit refuses extra policies,
Exclude rules, alternate authentication paths, OPTIONS bypass and additional
hostnames. It also refuses another application covering a preview hostname or
path, since a more specific Access application can override the wildcard policy.
Extending these rules requires an explicit review and matching audit changes.

Cloudflare Access supports `dev-*` in its application hostname matcher. This is
not a DNS partial-label wildcard: each future preview will have its own proxied
DNS record, managed by the deployment adapter.

## Protected GitHub environment

Keep the existing **preview-deployment** environment restricted to trusted `main`.
Add the following values there, not to the repository source or application
configuration:

| Type | Name | Meaning |
| --- | --- | --- |
| Secret | `CLOUDFLARE_API_TOKEN` | Scoped API management token |
| Variable | `CLOUDFLARE_ACCOUNT_ID` | Cloudflare account ID |
| Variable | `CLOUDFLARE_ZONE_ID` | Preview DNS zone ID |
| Variable | `CLOUDFLARE_TEAM_DOMAIN` | `agentstration.cloudflareaccess.com`, without scheme/path |
| Variable | `CLOUDFLARE_PREVIEW_DOMAIN` | `agentstration.cloud`, without scheme/wildcard |
| Variable | `CLOUDFLARE_ACCESS_APP_ID` | Access application ID, **not** its AUD tag |
| Variable | `CLOUDFLARE_ACCESS_CLIENT_ID` | CI service token Client ID ending in `.access` |
| Secret | `CLOUDFLARE_ACCESS_CLIENT_SECRET` | CI service token Client Secret |

The API token uses these permissions, limited to the selected account and only
the preview DNS zone:

| Type | Permission | Level |
| --- | --- | --- |
| Account | Cloudflare Tunnel | Edit |
| Account | Access: Apps and Policies | Read |
| Account | Access: Organizations, Identity Providers, and Groups | Read |
| Zone | DNS | Edit |

The standalone audit only exercises reads. The owned deployment lifecycle uses
the tunnel/DNS Edit permissions. No Access policy write permission is required: the
deployer must not silently weaken the human access gate. Avoid an IP filter tied
to one hosted GitHub runner, whose address can change. Track and renew token
expirations. An API token is separate from the CI service token: one manages
infrastructure, while the other authenticates bounded application probes.

Both trusted-main callers (`preview-on-demand.yml` and
`preview-azure-environment.yml`) explicitly use `secrets: inherit` when calling
`preview-environment.yml`. This opt-in is required for secrets to resolve in the
called job's protected environment, even though its public environment variables
are already available. The callee retains `environment: preview-deployment` and
the main-only execution boundary. Keep the secrets at environment scope; no copy
to repository variables or request JSON is needed. Inheritance can also expose
repository/organization secrets to the trusted callee; never add it to the
credential-free PR validation caller. See the
[GitHub workflow reuse guidance](https://docs.github.com/en/actions/how-tos/reuse-automations/reuse-workflows)
and [upstream runner report](https://github.com/actions/runner/issues/4453).

## Run the read-only audit

After this workflow is merged to `main`, open **Actions → Preview deployment
contracts → Run workflow**, select **main**, and enable **cloudflare_access**.
Leave the optional published-image and pricing checks disabled unless needed.
The ordinary offline contract jobs still run alongside the opt-in audit.

```bash
gh workflow run preview-azure-validation.yml --ref main -f cloudflare_access=true
```

The live job is absent from PR/push runs and refuses non-main dispatches. It uses
the protected environment and repository read access, with no Azure login,
OIDC, GHCR access or selected application checkout. The script independently
checks the invocation boundary when run in Actions. Requests go only to the fixed
Cloudflare API origin, reject redirects and bound payloads, timeouts and inventory
pagination. API failure bodies and credentials are never printed or uploaded.

The audit checks required values, the account's team domain, exact wildcard
application scope, Entra group support, the two policies, overlapping application
inventory, and readable tunnel/DNS inventories. Its small artifact and summary
report **configuration-validated**, **configured-not-exercised** for the service
credentials, the public application AUD and a null URL. The route reuses this
validated audience snapshot. A successful configuration check does **not** prove:

- that the supplied Client ID/Secret corresponds to the service token in the
  policy, is unexpired or can authenticate to a deployed application;
- that tunnel/DNS writes are authorized, that the configured zone belongs to the
  intended preview domain, or that a tunnel can reach the VM;
- that the named Entra group contains the intended people or that an authorized
  human can use a particular deployed preview;
- that public-origin bypass is impossible or that credentials are stripped before
  an application receives traffic.

These are acceptance checks for the deployed route. The default
Python suite tests policy rejection, scope isolation, credential-safe HTTP
failures, pagination and the trusted-workflow boundary without network access:

```bash
python3 -m unittest discover -s scripts/preview -p 'test_*.py' -v
```

## Owned route lifecycle

Run **Preview on demand** from `main` with `operation: deploy` and an environment
ID such as `pr-123`. The adapter records its Cloudflare scope, owner-derived
tunnel name and resource IDs in the existing private Azure ownership blob, under
the same exclusive lease as ARM mutations. It saves creation intent before API
writes and reconciles interrupted tunnel/DNS creation against that intent. A
foreign tunnel, DNS target, owner comment or changed account/zone is an error;
the adapter never replaces it. Restore the recorded scope before teardown if
environment variables have been changed.

The guest runs the images pinned in `deploy/preview/access.json`: `cloudflared`
for the outbound tunnel and Caddy for a local HTTP/WebSocket proxy. Neither is
part of the application Compose project. All incoming Azure ports remain denied;
application/proxy/connector metrics listeners bind loopback. Cloudflare terminates
public HTTPS. Caddy does not use ACME or create certificates. The exact-host
tunnel ingress requires an Access JWT for the audited AUD/team and ends with a
404 catch-all. The connector receives only its own tunnel token through ARM
protected parameters and a root-only file mounted exclusively into that container.
The token is removed from the environment before any Docker/Compose subprocess.

Caddy strips all `Cf-Access-*` request headers and the `CF_Authorization` cookie
before the selected application sees a request. It preserves other cookies and
application `Authorization`, supports WebSockets, and sets the public Host and
HTTPS forwarding headers. Application containers receive no management token,
CI probe credential, connector token, Docker socket or host configuration mount.
Infrastructure container logging is disabled. Do not enable request-header/body
logging while investigating a credential-bearing request.

Deployment always verifies guest image/source identity, loopback binding,
connector readiness, observed Azure NIC/NSG ingress and current Access/ingress
configuration. The optional `test_profile: none` does not disable these gates.
It then verifies anonymous and invalid-token denial and authenticated readiness
over HTTPS. Probe requests never follow redirects or export response bodies,
cookies or credentials. Only then does the result contain the stable HTTPS URL.
`status` repeats the protection checks before returning it; an unhealthy/stopped
guest returns no URL. The result exposes public tunnel/DNS IDs, never tokens.

Redeploy withdraws the old DNS record before updating the guest. A failed deploy
withdraws its owned DNS publication, retains ownership metadata and can be retried.
Inspect status and the protected state after an interrupted run; the existing
infinite Azure lease must be recovered before another operation can proceed.
`destroy` removes DNS, stops/deallocates or deletes the VM, clears stale tunnel
sessions and removes the owned tunnel. This also works when the VM is already
absent. Preserve retains server/Worker identities and application data; reset
removes them. The shared Access application, its policies, DNS zone and unrelated
environments are retained. DNS deletion alone is not immediate revocation because
of caches: connector JWT validation and stopping the connector remain essential.

## Qualification and validation

The real-image smoke exercises HTTP and a WebSocket upgrade/echo through the
pinned proxy, credential removal, preservation of application authentication,
and parsing of the connector's guarded ingress. It uses only disposable fixtures:

```bash
python3 scripts/preview/access_smoke.py
```

It requires Linux Docker, free loopback ports 5080/5081 and initial access to the
public pinned infrastructure images; it needs no cloud credentials or model.
The same smoke runs in **Preview deployment contracts** on PRs and `main`.

After merge, qualify a manually deployed SQLite preview, then PostgreSQL, with
the configured protected environment. Record real HTTPS/CI probe evidence, an
authorized Entra member login, a denied non-member login, public-IP denial and
the browser/Blazor WebSocket journey. Exercise redeploy, preserve/recreate and
explicit reset, verify unchanged OCI digests and verify no owned DNS/tunnel is
left after destruction. Local proxy checks are not evidence of live Entra
authorization. Access protects the route; the application retains its own
bootstrap/account and authorization model. No Development fixture is enabled.
PR/TTL automation remains #1286 and reusable deployed Playwright journeys #1287.
