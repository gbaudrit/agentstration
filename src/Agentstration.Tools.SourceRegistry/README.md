# Agentstration Source Registry tool

`Agentstration.SourceRegistry.Tool` is an offline .NET local tool for validating Agentstration `SourceVersion` manifests, calculating their canonical digest, and validating or building indexed static Registry publications.

```text
agentstration-source-registry source validate source.yaml
agentstration-source-registry source digest source.yaml
agentstration-source-registry registry validate index.yaml --publication-root . --base-uri https://example.test/v1/
agentstration-source-registry registry build index.yaml --publication-root . --base-uri https://example.test/v1/ --output ./published
```

The tool performs no network access and does not load the Agentstration server. It accepts either a complete `SourceRegistryIndex` or a direct `SourceRegistry` shard. Index construction emits canonical index and shard JSON/digest pairs plus the exact, deduplicated referenced Source Version manifest files.

See the Agentstration documentation for installation, exact-version pinning, exit codes, and CI usage.
