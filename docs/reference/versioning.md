# Versioning strategy

Several independent versions coexist in Agentstration. They must not be substituted for one another.

| Version | Example | What changes it |
| --- | --- | --- |
| Product version | `0.7.0` | A product release under Semantic Versioning. |
| HTTP API version | Current unversioned `/api` routes | A breaking HTTP contract change, not every product release. |
| Resource `apiVersion` | `agentstration.io/v1` | The schema of a declarative Management resource. |
| Resource revision/generation | Agent generation `3` | A change to one resource instance or immutable snapshot. |
| Documentation version | `Next`, later `1.x` | A supported major documentation line. |

## Product version

Agentstration intends to use Semantic Versioning: `MAJOR.MINOR.PATCH`, with prereleases such as `0.7.0-alpha.1`, `0.7.0-beta.1`, and `0.7.0-rc.1`. During `0.x`, public contracts remain under development.

The repository currently has no central product version property and no Git release tags. This documentation records the policy without inventing or changing a current release number.

## HTTP API version

An HTTP API version represents a compatibility boundary. Product `1.0`, `1.5`, and even `2.0` could continue to expose the same API version if the contract remains compatible.

The current API is not uniformly path-versioned. Management uses short `/api/...` routes and carries its schema version in the resource document. Workplace retains its own contract versioning. Introducing `/api/v1` remains a separate future HTTP decision.

## Resource apiVersion

`apiVersion` identifies a resource schema, not the Agentstration release and not an instance revision. The Management schema is `agentstration.io/v1`.

## Resource revision and generation

An Agent's internal generation changes when its functional declaration changes. Immutable Agent revisions capture the directly declared definition. Other aggregates also use versions or ETags for concurrency. These instance-level values do not replace `apiVersion` or the immutable resource UID.

## Flow versions

Flow publication uses immutable semantic versions such as `1.0.0`. This is the version of one Flow definition, not the product version.

## Documentation versions

During `0.x`, this site exposes a single current line: **Next**. It does not duplicate content for every `0.x` release. After the first stable release, Docusaurus versioning can freeze supported major lines such as `1.x` and `2.x`, while `Next` continues to describe development.
