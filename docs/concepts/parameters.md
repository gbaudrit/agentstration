# Parameters

A Parameter is reusable, nonsecret configuration owned by an instance, tenant, or workspace scope. Its value is intentionally readable by authorized operators through the Management API. Use a Secret instead when a value must be write-only or protected by a Vault.

Each Parameter stores a display name, optional description, scalar type, bounded JSON value, and descendant-use policy. Supported types are `string`, `integer`, `number`, and `boolean`; the serialized value is limited to 65,536 UTF-8 bytes.

References are exact: they contain the Parameter name, namespace, and owning scope. Agentstration never searches ancestors by name. A consumer in the owning scope may use the Parameter directly. A descendant consumer needs an explicit matching grant; sibling, cross-tenant, and descendant-to-ancestor use is denied.

The `/api/parameters` CRUD boundary uses ETags and validates values, metadata, scopes, and grants before persistence. Usage lookup is available at `/api/parameters/{name}/usages?scopeRef=...`, and deletion fails while a managed resource reports an exact reference. Resolution reads the current resource and policy for every invocation, so updates and revoked grants take effect immediately.

Although Parameters are not Secrets, execution and diagnostic code treats their resolved values as workload inputs: values are omitted from histories and logs by default.
