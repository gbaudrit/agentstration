# HTTP API and OpenAPI

Both ASP.NET Core API hosts register `AddOpenApi()` and expose the generated document in Development or Testing. The default document is available at:

```text
GET /openapi/v1.json
```

Use `http://localhost:5100/openapi/v1.json` for the combined authoritative server. OpenAPI is the source of truth for complete route and schema enumeration.

The current repository does not include Scalar or another OpenAPI UI. Adding a UI or generating reference pages from the document is **planned**; this iteration does not duplicate all endpoint schemas in Markdown.

Current API families include:

- Management resources under `/api/agents`, `/api/deployments`, and related short routes;
- Runtime Runs under `/api/runtime/runs`;
- Work Items under `/api/work/workitems`;
- Workplace routes under `/api/workspaces/{workspaceName}`;
- Flow definitions and Runs under `/api/flows` and `/api/flowRuns`;
- model providers/profiles and runtime profiles under `/api/...`;
- the earlier content, memory, and mission routes under `/api/workspaces/{workspaceId}`.

Problem Details, ETags/conditional writes, pagination, and `202 Accepted` are used where their implemented boundary requires them. API versioning is explained in [Versioning strategy](../versioning.md).
