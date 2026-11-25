# Shared preview access

[#1285](https://github.com/gbaudrit/agentstration/issues/1285) protects manually
deployed previews through Cloudflare Tunnel and a shared Access policy. The
selected domain is `agentstration.cloud`; PR hostnames will be
`dev-pr-123.agentstration.cloud`. The existing `agentstration.io` website and
`docs.agentstration.io` DNS remain independent.

The first executable increment is a **read-only configuration audit**. It does
not deploy a VM, create a tunnel or DNS record, or publish an application URL.
The existing [manual deployment](preview-on-demand.md) still returns no public
URL. #1284 and #1285 remain open until the protected route has been exercised.

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

The audit only exercises reads. Edit permissions are reserved for the future
owned tunnel/DNS lifecycle. No Access policy write permission is required: the
deployer must not silently weaken the human access gate. Avoid an IP filter tied
to one hosted GitHub runner, whose address can change. Track and renew token
expirations. An API token is separate from the CI service token: one manages
infrastructure, while the other will authenticate bounded application probes.

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
credentials, and a null URL. A successful configuration check does **not** prove:

- that the supplied Client ID/Secret corresponds to the service token in the
  policy, is unexpired or can authenticate to a deployed application;
- that tunnel/DNS writes are authorized, that the configured zone belongs to the
  intended preview domain, or that a tunnel can reach the VM;
- that the named Entra group contains the intended people or that an authorized
  human can use a particular deployed preview;
- that public-origin bypass is impossible or that credentials are stripped before
  an application receives traffic.

These are acceptance checks for the next deployment increment. The default
Python suite tests policy rejection, scope isolation, credential-safe HTTP
failures, pagination and the trusted-workflow boundary without network access:

```bash
python3 -m unittest discover -s scripts/preview -p 'test_*.py' -v
```

## Remaining implementation verticals

1. **Owned route lifecycle**: reuse the existing Azure ownership blob, provider
   lease and guest transport from ADR-0257/0258. Record tunnel and per-preview DNS
   identities with the preview owner. Refuse foreign resources, keep retries
   idempotent, tear down the route on destroy and reconcile interrupted creation.
   Keep Cloudflare integration outside the Runtime and the portable image format.
2. **Protected guest connection**: run a digest-pinned connector with only its
   own tunnel credential, validate Access JWTs at the connector, and retain the
   existing closed Azure ingress. Strip machine authentication headers and Access
   bearer credentials before requests reach selected PR code. Management and
   probe credentials stay in trusted CI. Support browser/WebSocket traffic.
3. **Live qualification and URL publication**: verify no unauthenticated or
   invalid-token request reaches the origin; exercise the scoped machine probe,
   authorized human login and direct-IP denial. Verify SQLite and optional
   PostgreSQL still work, redeploy/preserve/reset and partial-failure cleanup.
   Publish the stable HTTPS URL only after the protection checks succeed. PR/TTL
   automation remains #1286 and reusable deployed Playwright journeys #1287.

This guide records the staged plan; these three routing verticals are not yet
implemented by the configuration audit.
