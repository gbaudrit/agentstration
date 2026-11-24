# Local database previews

This implements the local database profiles of [#1281](https://github.com/gbaudrit/agentstration/issues/1281):
the authoritative server with its embedded Console, one independent MAF Worker,
SQLite by default, PostgreSQL as an explicit option, and no inference service. Selectable AEP extensions, the
separate Workplace shell and cloud provisioning remain subsequent increments.
The existing source-built Development topologies remain under `deploy/compose`.

Requirements: a Linux Docker host, Compose v2, Python 3 and authenticated pull
access to the private GHCR packages. Deployment scripts use only Python's standard
library; provisioning and registry credentials stay outside the product Runtime.

## Prepare an exact delivery

Use `delivery.json` receipts from the common **OCI images** workflow for catalogue
IDs `server` and `maf-worker`. The server follows the product version in
`Directory.Build.props`; the Worker keeps its own version and source revision.
Download each selected image's receipt, not a mutable branch tag. Ensure both
selected digests have actually been published to private GHCR.

```sh
python3 scripts/preview/preview.py prepare \
  --environment-id example \
  --directory .agentstration-preview/example \
  --server-delivery /path/to/server/delivery.json \
  --worker-delivery /path/to/worker/delivery.json

python3 scripts/preview/preview.py compose \
  --directory .agentstration-preview/example -- pull

python3 scripts/preview/preview.py compose \
  --directory .agentstration-preview/example -- up --detach --wait --wait-timeout 180

python3 scripts/preview/preview.py check --directory .agentstration-preview/example
```

The default URL is `http://127.0.0.1:5080`. Use `--port` to select another host
port; port `0` allocates a temporary port for isolated smoke tests. The Worker has
no host-published port. Only loopback HTTP is supported in this increment;
external routing, TLS and protected URLs belong to #1283–#1285.

`deployment.json` records environment ID, database selection, stable instance/Worker/
credential IDs, the server source SHA and each image's own source/version/digest.
The Compose project is `preview-<environment-id>`; use a distinct ID and state
directory for each preview. Repeating `prepare` for the same directory preserves
identities and the key, while allowing explicitly selected image receipts to be
updated. An existing directory cannot be reassigned to another environment.
Do not reuse an environment ID with unrelated retained volumes or a fresh state
directory. Provider inventory and orphan reconciliation belong to #1282/#1286.

The helper supplies exact images and project identity from recorded state,
disables ambient Compose profiles/files and ignores `.env` interpolation files.
There are no Compose `build` directives. `check` validates container image
references, OCI source/version labels, server readiness, authenticated Worker
readiness and absence of Worker database mounts/settings. Its JSON contains
technical metadata, not credentials.

## Optional PostgreSQL profile

Select the database when preparing a **new** preview:

```sh
python3 scripts/preview/preview.py prepare \
  --environment-id example-pg \
  --directory .agentstration-preview/example-pg \
  --database postgresql \
  --server-delivery /path/to/server/delivery.json \
  --worker-delivery /path/to/worker/delivery.json
```

Use the same `compose ... -- pull`, `compose ... -- up` and `check` commands with
that directory. The helper adds `deploy/preview/postgresql.yml` to the base
composition; the application image digests do not change with the database choice.
The official third-party PostgreSQL 17 image is pinned by digest in
`deploy/preview/postgresql.json`, runs as `linux/amd64`, and is recorded in the
deployment alongside the private GHCR application images. Consuming this image
from Docker Hub does not publish any Agentstration image there.

The database has no host-published port. Its random password is persisted in the
protected `postgres.password` file and mounted through `POSTGRES_PASSWORD_FILE`.
Only the server's private JSON contains the corresponding connection string;
neither the deployment record nor the Compose environment/model contains its
value. The Worker still receives only its AWP credential and no database access.
Each preview owns a separate PostgreSQL volume **and** the existing server data
volume, which retains file-backed keys, archives and artifacts in both profiles.
This reuses [ADR-0078](../decisions/0078-postgresql-is-an-optional-server-storage-profile.md).

Repeating `prepare` without `--database` preserves an existing profile. Changing
the database for existing state is rejected: create another preview with a distinct
environment ID and state directory. No SQLite/PostgreSQL data conversion is
performed. A missing or symlinked PostgreSQL password file is rejected and is
never silently regenerated. Restore the matching original state and volumes.

To select another approved PostgreSQL 17 build, supply `--postgres-image` with
`docker.io/library/postgres:17@sha256:<digest>`. Floating tags, other image
repositories and major-version changes are rejected. Omitting the option on
repeat keeps the recorded image, even when the repository default changes. Test
and back up before changing any image; major upgrades require their own explicit
migration procedure and are outside this helper.

## Credentials and initialization

State directories have mode `0700`; files have mode `0600`. Keep state outside
the repository or under an ignored `.agentstration*` directory. Git and Docker
build context rules exclude those directories. The helper refuses unrelated
nonempty directories, state-file symlinks and silent regeneration of a missing
key for an existing preview.

The random Worker key and server JSON configuration are mounted read-only as
file-backed Compose secrets. The Worker receives only its own key, never the
server database or a connection string. These are protected host files, not an
encrypted secret manager. Do not upload the state directory or run `compose config`
with additional overrides that interpolate secret values.

Initial Bootstrap is disabled by default and no administrator password is
supplied. Open `/bootstrap` to perform the existing manual initialization.
For explicit initial data, `prepare --bootstrap-config /path/to/bootstrap.json`
accepts the existing `Agentstration:Bootstrap` JSON object. The image contains the
versioned catalogue at `/app/bootstrap/profiles`. Credentials supplied through
Bootstrap configuration are saved only in the protected server configuration;
never use the public Development `admin / admin` fixture in a preview.

Omitting `--bootstrap-config` on repeat preserves the selected configuration.
Apply configuration changes with `up --detach --force-recreate --wait`; this does
not add a new live-seeding feature to the Runtime. Profile composition and scope
rules remain those of the existing [Bootstrap reference](../reference/declarative-bootstrap.md).

## Stop and preserve data

```sh
python3 scripts/preview/preview.py compose \
  --directory .agentstration-preview/example -- down
```

This preserves the project's named data volumes and host-side credentials.
Retain the matching state directory. An intentional reset uses `down --volumes`
for this exact project before removing its host state. No host-wide prune is part
of preview cleanup. No automatic rollback after schema migration is promised.

Keep every OCI index, platform manifest and attestation needed by an active
preview, together with its delivery receipts. Age or an untagged status alone
does not prove that a manifest is unused. Automated retention/inventory cleanup
remains #1286; image publication and evidence artifact retention remain those of
the common packaging workflow.

## Validation and promotion

```sh
python3 -m unittest discover -s scripts/preview -p 'test_*.py' -v
```

The server's OCI validation builds one disposable Worker companion with the same
source and pinned SDK/runtime bases, stages both exact archives in a loopback
registry, and runs `scripts/preview/preview_smoke.py` against this composition.
Both SQLite and PostgreSQL must pass using those **same application archives**
before the server can be promoted. The test first boots the non-Development default without Bootstrap and rejects
the public fixture credential. It then explicitly selects the existing test
profiles with a newly generated password, authenticates a Worker, executes a
deterministic Flow, recreates all selected containers while retaining data and
proves the previous result is retained and a second execution succeeds.
It also checks server file-volume persistence; for PostgreSQL it verifies tables
exist in all seven module-owned schemas. Only the disposable test project's own
containers and volumes are removed, on success or failure.

The companion is a CI fixture and is not automatically published as a Worker
release. Each profile's exact tested images are recorded in
`preview-sqlite-smoke.json` and `preview-postgresql-smoke.json`.
Real deployments select published receipts independently; the tested pair is
not a promise of compatibility with every historical Worker version. To exercise
a particular published pair, run the same disposable smoke after authenticating
the Docker client, using its two receipts, `--registry ghcr.io` and
`--database sqlite` or `--database postgresql`.

The server archive is promoted through the existing private GHCR publisher,
without rebuilding. Its package `agentstration-server` must already be private
and grant this repository **Write** under **Manage Actions access**. The publisher
refuses inaccessible or non-private packages before uploading application content.
Official product release tags and Docker Hub publication are unchanged.
