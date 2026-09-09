# Agentstration Source Registry tool

`Agentstration.SourceRegistry.Tool` is an offline .NET local tool for validating Agentstration `SourceVersion` manifests and calculating their canonical digest.

```text
agentstration-source-registry source validate source.yaml
agentstration-source-registry source digest source.yaml
```

The tool performs no network access and does not load the Agentstration server. `SourceRegistry` validation and deterministic publication generation remain deferred until the registry contract in Agentstration issue #232 is finalized.

See the Agentstration documentation for installation, exact-version pinning, exit codes, and CI usage.
