# Source Registry publication tool

`Agentstration.SourceRegistry.Tool` is a versioned .NET local tool for inspecting publisher-authored Agentstration manifests without starting the Agentstration server. The installed command is `agentstration-source-registry`.

## Install and pin an exact version

Create a repository-local tool manifest once, then install an exact published version:

```text
dotnet new tool-manifest
dotnet tool install Agentstration.SourceRegistry.Tool --version <exact-version>
```

Commit `.config/dotnet-tools.json`. Other contributors and CI restore the pinned version with:

```text
dotnet tool restore
```

The tool shares the Agentstration product version because it implements that release's Source contracts. Relevant pushes to `main` publish timestamped development packages with the form `<product-version>.dev.<UTC timestamp>.<run-id>.<attempt>` when the product version already has a prerelease suffix, or `<product-version>-dev...` otherwise. These CI snapshots must always be pinned by their full exact version. The same dedicated workflow publishes official packages from the product's `v<version>` tag: suffixed Semantic Versions are NuGet prereleases and versions without a suffix are stable releases. Publication uses short-lived OIDC credentials. No consumer credential is required to install or restore it.

Update an existing local pin deliberately to another exact version:

```text
dotnet tool update Agentstration.SourceRegistry.Tool --version <exact-version>
```

Remove the local tool from the current manifest with:

```text
dotnet tool uninstall Agentstration.SourceRegistry.Tool
```

## Commands

Validate one `SourceVersion` document through the same strict reader used by Agentstration imports:

```text
dotnet tool run agentstration-source-registry -- source validate sources/agentstration/bootstrap-samples/1/source.yaml
```

Print only its canonical digest:

```text
dotnet tool run agentstration-source-registry -- source digest sources/agentstration/bootstrap-samples/1/source.yaml
```

The digest has the stable form `sha256:<lowercase-hex>`. Property order, YAML comments, and presentation whitespace do not change it. Functional scalar types and array order remain significant.

Successful validation prints the validated Source identity, opaque version, and digest. Failures print one path-qualified diagnostic to standard error and never print the full manifest.

Validate a complete local Registry publication without modifying it:

```text
dotnet tool run agentstration-source-registry -- registry validate index.yaml \
  --publication-root . \
  --base-uri https://example.test/v1/
```

Build the deterministic static publication tree:

```text
dotnet tool run agentstration-source-registry -- registry build index.yaml \
  --publication-root . \
  --base-uri https://example.test/v1/ \
  --output ./published
```

Both commands accept either a direct `SourceRegistry` shard named `registry.*` or a complete `SourceRegistryIndex` named `index.*`. Index validation follows every declared shard offline, verifies its canonical digest, checks Channel compatibility intersection with the shard's Semantic Version interval, and applies cross-shard conflict and shared-manifest rules. Both forms validate safe same-origin URL mapping, duplicates, shard-local `latest`, limits, and every referenced Source Version identity, opaque version, and canonical digest.

An index build emits only the complete reachable publication:

```text
published/
  index.json
  index.sha256
  registry-<catalog>.json
  registry-<catalog>.sha256
  sources/<publisher>/<source>/<version>/source.yaml
```

Index and shard documents contain RFC 8785 canonical JSON without a trailing newline. Their `.sha256` files contain the corresponding `indexDigest` or `registryDigest` followed by LF. Referenced manifests are copied byte-for-byte and identical shared targets are emitted once; unreferenced and hosting-specific files are not copied. Input and output trees must not overlap, and links, reparse points, unsafe paths, case collisions, and a non-empty output directory are rejected. Direct shard builds remain available and emit `registry.json`, `registry.sha256`, and their manifests.

## Exit codes

| Code | Meaning |
| --- | --- |
| `0` | The command completed successfully. |
| `1` | The manifest failed decoding, parsing, or contract validation. |
| `2` | The command line is not supported or is incomplete. |
| `3` | The input path cannot be read. |
| `130` | The operation was cancelled. |

## GitHub Actions

Restore the exact version before the offline validation step:

```yaml
- uses: actions/setup-dotnet@v6
  with:
    global-json-file: global.json
- run: dotnet tool restore
- run: >-
    dotnet tool run agentstration-source-registry -- registry validate index.yaml
    --publication-root .
    --base-uri https://registry.example/v1/
```

Tool restoration may contact the configured package source. Command execution after restoration performs no network access.

## Registry contract

The commands implement the executable static Registry v1 contract defined by [#232](https://github.com/gbaudrit/agentstration/issues/232). `SourceRegistryIndex` is the small release-line index and `SourceRegistry` remains a bounded catalogue shard. The shared readers own schema validation, canonical ordering and separate index/shard digests; every referenced `SourceVersion` still goes through the production `SourceManifestReader` used by Agentstration imports.

## Boundary

This tool validates publication inputs. It does not fetch a remote registry, register one with Agentstration, evaluate trust, import Sources, materialize Channels, install Packs, or publish GitHub Pages. Those remain separate runtime, Management, Source Provider, and hosting concerns.
