# ADR-0273: Preview extension recipes own optional dependencies

Status: Proposed — 2026-11-28

## Context

The PostgreSQL document extension delivered by [#1386](https://github.com/gbaudrit/agentstration/pull/1386)
requires a document database when used. It is independent of the optional
PostgreSQL control-plane profile. Preview selection and cleanup (#1286) need to
handle this dependency without introducing extension branches throughout the
provider adapters or making PostgreSQL mandatory for Agentstration.

## Decision

Keep an explicit, small registry of trusted deployment recipe modules under
`scripts/preview/extension_deployments`. Core selection, Compose generation,
image transport, state retention and cleanup delegate to that registry. Recipe
descriptors contain data, never executable paths from candidate PRs. Azure bundles
the trusted modules and descriptors; Runtime receives no deployment tooling.
Existing extensions without dependencies retain their generic AEP recipe.

Selecting `preview:extension:postgresql` defaults to a separate internal PostgreSQL
17 document container and owned persistent volume. Pin its official image by
digest in the public request. Publish no database port. SQLite remains the core
default, including when this extension is selected. The additional
`preview:extension:postgresql:externalDB` modifier suppresses the managed database.
Reject modifiers without their base extension and unknown or duplicate modifiers.
Freeze and recheck the mode for command admission and automatic reconciliation.

Use deployment contract version 5 for recipe selections, preserving independent
requested and delivered source revisions from version 4. Versions 1, 2 and 4
remain readable; legacy receipts do not implicitly acquire managed dependencies.
Version 3 remains reserved by the parallel deployment-profile change (#1422).
An internal database requires an extension receipt advertising recipe version 1,
which includes the exact-host TLS opt-in. Older images remain usable in external
mode. Missing compatibility fails before infrastructure allocation.

Generate the internal database password on the target host, never in CI. Keep
it in a private 0600 file and mount it through Compose secrets. A server startup
wrapper reads that file locally and exports the existing Bootstrap secret
configuration key before executing ASP.NET Core. Docker configuration contains
the script and secret paths, never the password. The process environment and
private files remain readable by an authorized host administrator. This is not
an additional secret store or protection against host root.

Secured AEP exports require an HTTPS callback reachable from the extension.
Reuse Kestrel on an unpublished internal port rather than weakening the HTTPS
rule or adding a proxy. A small external helper uses target OpenSSL to generate
the preview's certificate/key pair. Mount only its public certificate into
selected extensions as additional runtime trust; mount the private key only into
the authority. Reuse the identity across updates/deselection and erase it at
reset. Bootstrap credentials and TLS keys never cross CI or public feedback.

External connection settings can be provisioned directly as a bounded private
JSON file on the target or supplied through the existing protected extension
configuration channel. Strip them from extension settings and public results.
Do not generate a password for an existing external database. Keep TLS VerifyFull
by default; only the isolated internal hostname is explicitly allowed without
TLS. Configure the allowed authenticated caller to the preview's instance ID.

Expose nonsecret connection parameters and the secret key to the existing
Bootstrap mechanism. Supply an optional Instance profile with a Schema, Vault,
Secret, Connected Service and Storage. Do not enable bootstrap, create an
administrator or choose a Tenant/Workspace automatically. Automatic PR profile
selection remains the separate #1419 vertical.

Ordinary updates and deselection/reselection preserve database credentials,
volume, identity and the expiry anchor. A database mode or external connection
change requires explicit reset. Reset and existing automatic expiry/close cleanup
remove owned internal resources and local credentials, including deselected
dependencies. They never connect to or delete an external database.

## Consequences

New dependencies can extend the small trusted registry without copying core
workflows. This does not introduce a plugin executor or a deployment framework.
Public health includes managed dependencies; feedback describes the chosen mode.
Offline tests cover selection, compatibility, credential isolation and lifecycle
guards. The existing generic AEP Docker smoke additionally verifies managed
database health, cluster continuity, local secret handoff and complete reset.
Real cloud qualification and publication of the compatible private image remain
separate from offline and local Docker validation.
