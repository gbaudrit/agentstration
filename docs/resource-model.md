# Resource model

Every resource has `id`, `name`, `type`, `apiVersion`, optional `resourceGroup` and `location`, tags, and an ETag.

Provider namespaces are:

- `Agentstration.Agents`
- `Agentstration.Models`
- `Agentstration.Tools`
- `Agentstration.Runtime`
- `Agentstration.Memory`
- `Agentstration.Identity`

Public APIs deliberately use resource-provider terminology rather than Kubernetes concepts. PUT is idempotent. ETags provide optimistic concurrency, while immutable resources such as published revisions can only be created once.
