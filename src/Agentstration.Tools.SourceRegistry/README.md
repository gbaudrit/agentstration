# Agentstration Source Registry tool

`Agentstration.SourceRegistry.Tool` is an offline .NET local tool for validating Agentstration `SourceVersion` manifests, calculating their canonical digest, and validating or building static `SourceRegistry` publication trees.

```text
agentstration-source-registry source validate source.yaml
agentstration-source-registry source digest source.yaml
agentstration-source-registry registry validate registry.yaml --publication-root . --base-uri https://example.test/
agentstration-source-registry registry build registry.yaml --publication-root . --base-uri https://example.test/ --output ./published
```

The tool performs no network access and does not load the Agentstration server. Registry construction emits only canonical `registry.json`, `registry.sha256`, and the exact referenced Source Version manifest files.

See the Agentstration documentation for installation, exact-version pinning, exit codes, and CI usage.
