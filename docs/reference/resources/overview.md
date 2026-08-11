# Declarative resources

Management resources use this canonical envelope:

| Field | Meaning |
| --- | --- |
| `id` | Server-derived canonical resource identifier. |
| `name` | Resource name within its type and resource group. |
| `type` | Provider namespace and resource type, for example `Agentstration.Agents/agents`. |
| `apiVersion` | Dated resource schema version. |
| `resourceGroup` | Logical grouping segment used in the canonical identifier. |
| `location` | Placement label; standalone resources normally use `local`. |
| `tags` | Optional string metadata. |
| `properties` | Desired configuration specific to the resource type. |
| `generation` | Functional instance generation maintained by the Management Plane. |
| `status` | Observed provisioning state and conditions. |
| `eTag` | Optimistic concurrency token. |

The implemented Management schema version is `2026-08-01`. Resource `apiVersion`, instance generation/revision, HTTP API version, and product version are distinct; see [Versioning strategy](../versioning.md).

Currently defined resource types include Agent Types, Agents, Agent Revisions, Deployments, Operations, Model Providers, Model Profiles, and Runtime Profiles. Their canonical contracts live in `Agentstration.Management.Abstractions`; transport requests live in `Agentstration.Management.Contracts`.
