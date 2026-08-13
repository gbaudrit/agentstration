# Resource model

Management resources use the Agentstration-native envelope described by ADR-0031:

```json
{
  "uid": "server-generated-guid",
  "apiVersion": "agentstration.io/v1",
  "kind": "Agent",
  "metadata": { "name": "sql-expert", "tags": {}, "annotations": {} },
  "definition": {}
}
```

`metadata.name` is the canonical human-readable identifier inside `(tenant, workspace, kind)`. References carry a `name` and, only when explicitly authorized in a future increment, an optional `workspaceRef`. HTTP routes therefore use short names such as `/api/agents/sql-expert` and never encode tenancy in the resource name.

The server owns immutable `uid` values. PUT is idempotent, ETags provide optimistic concurrency, and published revisions and Flow versions are immutable.

Flow and Workplace resources also use canonical names at their module boundary. They remain independent bounded contexts and do not reuse the Management envelope merely for visual uniformity.
