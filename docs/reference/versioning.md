# Versioning strategy

Several independent versions coexist in Agentstration. They must not be substituted for one another.

| Version | Example | What changes it |
| --- | --- | --- |
| Product version | `0.7.0` | A product release under Semantic Versioning. |
| HTTP API version | Future `v1`; current dated `api-version` on some surfaces | A breaking HTTP contract change, not every product release. |
| Resource `apiVersion` | `2026-08-01` | The schema of a declarative resource. |
| Resource revision/generation | Agent generation `3` | A change to one resource instance or immutable snapshot. |
| Documentation version | `Next`, later `1.x` | A supported major documentation line. |

## Product version

Agentstration intends to use Semantic Versioning: `MAJOR.MINOR.PATCH`, with prereleases such as `0.7.0-alpha.1`, `0.7.0-beta.1`, and `0.7.0-rc.1`. During `0.x`, public contracts remain under development.

The repository currently has no central product version property and no Git release tags. This documentation records the policy without inventing or changing a current release number.

## HTTP API version

An HTTP API version represents a compatibility boundary. Product `1.0`, `1.5`, and even `2.0` could continue to expose the same API version if the contract remains compatible.

The current API is not uniformly path-versioned. Management endpoints require the dated query parameter `api-version=2026-08-01`; Workplace resources use `2026-08-05` in their contracts; several `/api/...` routes are currently unversioned. Introducing `/api/v1` is **planned policy**, not an implemented migration.

## Resource apiVersion

`apiVersion` identifies a resource schema, not the Agentstration release and not an instance revision. Stable resource schemas use a date such as `2026-08-01`. A future experimental schema may use `YYYY-MM-DD-preview`; no preview resource schema is currently implemented.

## Resource revision and generation

An Agent's `generation` changes when its functional declaration changes. Immutable Agent revisions refer to a particular Agent generation and Agent Type version. Other aggregates also use versions or ETags for concurrency. These instance-level values do not replace `apiVersion`.

## Flow versions

Flow publication uses immutable semantic versions such as `1.0.0`. This is the version of one Flow definition, not the product version.

## Documentation versions

During `0.x`, this site exposes a single current line: **Next**. It does not duplicate content for every `0.x` release. After the first stable release, Docusaurus versioning can freeze supported major lines such as `1.x` and `2.x`, while `Next` continues to describe development.
