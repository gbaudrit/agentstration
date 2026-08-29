# Versioning strategy

Several independent versions coexist in Agentstration. They must not be substituted for one another.

| Version | Example | What changes it |
| --- | --- | --- |
| Product version | `0.1.0-alpha.1` | A product release under Semantic Versioning. |
| HTTP API version | Current unversioned `/api` routes | A breaking HTTP contract change, not every product release. |
| Resource `apiVersion` | `agentstration.io/v1` | The schema of a declarative Management resource. |
| Resource revision/generation | Agent generation `3` | A change to one resource instance or immutable snapshot. |
| Pack version | `agentstration/price-watch/1.2.0` | A release of one coherent distribution bundle. |
| Documentation version | `Next`, later `1.x` | A supported major documentation line. |

## Product version

Agentstration uses Semantic Versioning: `MAJOR.MINOR.PATCH`, with prereleases such as `0.1.0-alpha.1`, `0.1.0-beta.1`, and `0.1.0-rc.1`. During `0.x`, public contracts remain under development.

The product version is centralized in the root `Directory.Build.props`. The autonomous AEP workspace keeps its own package version under `aep/`; changing the Agentstration product version does not change the AEP protocol or packages.

Product prereleases use annotated Git tags named `v<version>`. Pushing a matching tag from a commit contained in `main` runs the release workflow, repeats the offline Release build and test suite, publishes framework-dependent server and Workplace archives, writes SHA-256 checksums, pushes a multi-platform server/Console image to Docker Hub, and creates a GitHub prerelease from the matching file under `docs/releases/`. A tag that disagrees with the central version or does not point into `main` fails closed. Prereleases publish an immutable version container tag and a moving channel tag such as `alpha`, but never `latest`.

## HTTP API version

An HTTP API version represents a compatibility boundary. Product `1.0`, `1.5`, and even `2.0` could continue to expose the same API version if the contract remains compatible.

The current API is not uniformly path-versioned. Management uses short `/api/...` routes and carries its schema version in the resource document. Workplace retains its own contract versioning. Introducing `/api/v1` remains a separate future HTTP decision.

## Resource apiVersion

`apiVersion` identifies a resource schema, not the Agentstration release and not an instance revision. The Management schema is `agentstration.io/v1`.

## Resource revision and generation

An Agent's internal generation changes when its functional declaration changes. Immutable Agent revisions capture the directly declared definition. Other aggregates also use versions or ETags for concurrency. These instance-level values do not replace `apiVersion` or the immutable resource UID.

## Flow versions

Flow publication uses immutable semantic versions such as `1.0.0`. This is the version of one Flow definition, not the product version.

## Pack versions

A Pack version identifies one release of a coherent resource bundle. Its conceptual coordinate is `publisher/name/version`, and the initial policy is Semantic Versioning. It does not replace the `apiVersion`, generation, ETag, or immutable publication version of any resource contained by the Pack. Local archive installation and modification-safe differential updates are implemented; dependency resolution and three-way merge remain planned.

## Documentation versions

During `0.x`, this site exposes a single current line: **Next**. It does not duplicate content for every `0.x` release. After the first stable release, Docusaurus versioning can freeze supported major lines such as `1.x` and `2.x`, while `Next` continues to describe development.
