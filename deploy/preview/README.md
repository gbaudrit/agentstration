# Local database previews

`compose.yml` consumes exact server and Worker image digests, with externally
prepared credentials and a per-preview SQLite/data volume. It never builds images
or starts an inference service.

SQLite is the default. The helper adds `postgresql.yml` only when preparing a
PostgreSQL preview. Both use the same application images. PostgreSQL's image is
pinned in `postgresql.json`; its password and data volume belong to that preview.

Use the [operating and validation instructions](../../docs/contributing/preview-environments.md)
for receipt selection, `scripts/preview/preview.py`, initialization, state
preservation and cleanup. This completes the local database profiles of #1281, not a cloud
provisioner or the source-built Development topology.

The [portable lifecycle guide](../../docs/contributing/preview-lifecycle.md)
documents managed deploy/status/destroy, ownership, safe result JSON and reusable
GitHub Actions entrypoints for the first #1282 increment.

Optional AEP dependencies use the trusted recipes in
`scripts/preview/extension_deployments`; descriptors live in `extensions/`.
Selecting the PostgreSQL document extension adds its own digest-pinned internal
database and volume, independently of this directory's core database profile.
The `preview:extension:postgresql:externalDB` modifier disables that dependency.
See [preview automation](../../docs/contributing/preview-automation.md#postgresql-document-database)
for private configuration, target-generated credentials and reset behavior.
