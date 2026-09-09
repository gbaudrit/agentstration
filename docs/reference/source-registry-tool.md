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

The package is prepared for exact-version installation and local-feed validation. Public NuGet publication will be enabled after the repository's trusted publishing profile is configured.

## Commands

Validate one `SourceVersion` document through the same strict reader used by Agentstration imports:

```text
dotnet tool run agentstration-source-registry source validate sources/agentstration/bootstrap-samples/1/source.yaml
```

Print only its canonical digest:

```text
dotnet tool run agentstration-source-registry source digest sources/agentstration/bootstrap-samples/1/source.yaml
```

The digest has the stable form `sha256:<lowercase-hex>`. Property order, YAML comments, and presentation whitespace do not change it. Functional scalar types and array order remain significant.

Successful validation prints the validated Source identity, opaque version, and digest. Failures print one path-qualified diagnostic to standard error and never print the full manifest.

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
- run: dotnet tool run agentstration-source-registry source validate sources/agentstration/bootstrap-samples/1/source.yaml
```

Tool restoration may contact the configured package source. Command execution after restoration performs no network access.

## Registry contract status

Issue [#232](https://github.com/gbaudrit/agentstration/issues/232) currently describes an illustrative `SourceRegistry` shape, but its canonical catalogue digest, final URL-resolution rules, duplicate policy, bounds, and schema-evolution rules are not yet executable production contracts. The tool therefore does not expose `registry validate` or `registry build` yet. Adding either command before those rules exist would create a second, incompatible registry authority.

The future Registry commands will reuse `SourceManifestReader` for every referenced `SourceVersion`, operate only on an explicit publication root or descriptor, copy no implicit files, and remain offline. Their implementation is tracked by #232 and #234.

## Boundary

This tool validates publication inputs. It does not fetch a remote registry, register one with Agentstration, evaluate trust, import Sources, materialize Channels, install Packs, or publish GitHub Pages. Those remain separate runtime, Management, Source Provider, and hosting concerns.
