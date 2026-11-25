# Nantes - Alertes

Opt-in **Instance** profile, available under **System > Bootstrap profiles**
(`/settings/bootstrap`). Select **Nantes - Alertes**, preview, satisfy any missing
input, review again, then apply. The default Development profile does not select it.
No PowerShell resource-creation script is needed.
The profile supports both manual application and initial Bootstrap. Initial
Bootstrap opens authenticated Worker communications after topology creation,
materializes built-in resources and required extension contributions, then runs
discovery before creating its consumers. A compatible AWP Worker must be able to
connect while the authority is still initializing; discovery waits are bounded
by `Agentstration:Bootstrap:DiscoveryTimeout`.

## Prerequisites

- Enrolled, available `Agentstration.Extensions.OpenApi` and
  `agentstration.extensions.postgresql` contributions, version `1.0.0`.
- OpenAPI extension configuration: `OpenApi:Enabled=true` and
  `OpenApi:AllowedDomains:0=data.nantesmetropole.fr`. Do not bypass destination
  policy. Extension launch/enrollment remains external to this profile.
- An available AWP Worker capable of the discovered acquisition/discovery Flows.
- PostgreSQL at `localhost:5432`, database `agentstration_documents`, user
  `agentstration`. Provision the database and role independently.
  The PostgreSQL extension must authorize the authority's AEP caller through
  `PostgreSqlDocuments:AllowedClients` or its normal paired-client policy.
  For this disposable loopback database without TLS, explicitly configure
  `PostgreSqlDocuments:AllowLoopbackWithoutTls=true` on that extension.
  Set `PostgreSqlDocuments:ProvisionSchema=true` to let it provision its bounded
  storage schemas, or provision those schemas separately. These are extension
  deployment policies, not settings overridden by the profile.
- On the authoritative Web server, supply
  `Agentstration:Bootstrap:Secrets:NantesPostgreSqlPassword` via User Secrets,
  environment variable
  `Agentstration__Bootstrap__Secrets__NantesPostgreSqlPassword`, or an already
  registered IConfiguration provider. With Aspire it may be supplied on AppHost:
  the Bootstrap subtree is forwarded to Web, with protected secret parameters.
  Never commit the password. Alternatively select/create a configured Secret in
  the preview's shared binding editor. Contextual creation requires an existing,
  initialized, authorized Vault; use the normal Vault screen to prepare one.

Optional `Agentstration:Bootstrap:Parameters:*` inputs and defaults:

| Suffix | Default |
| --- | --- |
| `NantesPostgreSqlHost` | `localhost` |
| `NantesPostgreSqlPort` | `5432` (integer) |
| `NantesPostgreSqlDatabase` | `agentstration_documents` |
| `NantesPostgreSqlUser` | `agentstration` |
| `NantesOpenApiUrl` | `https://data.nantesmetropole.fr` |
| `NantesNewsUrl` | Full Google News Nantes RSS request URL in the manifest |
| `NantesAlertsLimit` | `20` (integer) |

Nonempty configured values take precedence over authored defaults. Invalid types
are rejected. `Agentstration:Bootstrap:DiscoveryTimeout` defaults to ten minutes
and accepts one second through thirty minutes.

## Composition and limits

Thirteen ordered declarations publish the original Alertes Schema, prepare a
Vault/Secret, configure PostgreSQL and JSONB Storage, configure local content
Storage, configure Nantes OpenAPI and Google News HTTP services, discover and
activate the two explicitly selected operations, create their Data Sources, then
create the aggregating Knowledge Source. No model or transformation Flow is added.

The supplied published Schema retains its `$defs`, `$ref` and nullable `anyOf`.
The explicit acquisition Schema represents nullable geolocation with the equivalent
`type: [object, null]` declaration supported by the bounded acquisition validator.
Content is unchanged: Nantes retains `{total_count, results}`; RSS retains the
builtin `{mediaType, content}` envelope and original XML.

Nantes responses use JSONB's current 65,536-byte per-document limit. RSS and
Knowledge output use local Storage with a 10 MiB bound. The HTTP/Data Source
limits delivered in #1218 are reused. No truncation, splitting or RSS parsing is
performed. If a real response exceeds an existing limit, the normal acquisition
reports its failure.

The first preview does not contact upstream services; discovery-dependent checks
are marked deferred. Apply shows discovery/Worker waits and the Flow Run link.
Successful prerequisites remain after a failure. Retry preserves existing resources
and Secret values; operator edits and disabled operations require explicit review.

After successful application, open the two Data Sources and run their normal
acquisition actions, then publish a Knowledge Snapshot from **Nantes - Alertes**.
Profile application itself does not fetch content or install refresh schedules.

After a Release build, an opt-in isolated live check is available from the
repository root:

```powershell
./scripts/bootstrap/test-nantes-alertes.ps1 -Live -Acquire
```

It prompts for the PostgreSQL password, starts its own isolated authority,
extensions and Worker, applies the **catalogue** twice, performs explicit
acquisitions/projection, and stops only its own processes. It accesses the real
public endpoints and writes sample records to the configured development database.
