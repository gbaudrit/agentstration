# Resource model

Management resources use the Agentstration-native envelope described by ADR-0031:

```json
{
  "uid": "server-generated-guid",
  "apiVersion": "agentstration.io/v1",
  "kind": "Agent",
  "metadata": { "namespace": "default", "name": "sql-expert", "tags": {}, "annotations": {} },
  "definition": {}
}
```

`metadata.name` is the canonical human-readable identifier inside `(tenant, workspace, namespace, kind)`. The namespace defaults to `default`. References carry a `name`, an optional namespace, and, only when explicitly authorized in a future increment, an optional `workspaceRef`. An omitted namespace resolves relative to the owner resource's namespace. Existing routes such as `/api/agents/sql-expert` address `default`; explicit routes use `/api/namespaces/{namespace}/agents/{name}` and never encode tenancy in the resource name.

The server owns immutable `uid` values. PUT is idempotent, ETags provide optimistic concurrency, and published revisions and Flow versions are immutable.

Flow and Workplace resources also use canonical names and the same neutral namespace value at their module boundary. They remain independent bounded contexts and do not reuse the Management envelope merely for visual uniformity.
