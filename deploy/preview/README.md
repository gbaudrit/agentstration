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
