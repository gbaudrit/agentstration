# Local SQLite preview

`compose.yml` consumes exact server and Worker image digests, with externally
prepared credentials and a per-preview SQLite/data volume. It never builds images
or starts an inference service.

Use the [operating and validation instructions](../../docs/contributing/preview-environments.md)
for receipt selection, `scripts/preview/preview.py`, initialization, state
preservation and cleanup. This is the first local increment of #1281, not a cloud
provisioner or the source-built Development topology.
