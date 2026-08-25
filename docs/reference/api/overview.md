# HTTP API and OpenAPI

The authoritative ASP.NET Core server exposes its generated contract and Swagger UI in Development or Testing. The endpoints are:

```text
GET /openapi/v1.json
GET /swagger
```

Use `http://localhost:5100/swagger` to browse and invoke the API, or `http://localhost:5100/openapi/v1.json` to consume the OpenAPI 3.1 document. OpenAPI is the source of truth for complete route and schema enumeration. Every operation receives a stable operation identifier and a functional tag, and protected operations declare the supported JWT bearer and application-cookie authentication schemes.

Swagger uses an existing Console application cookie automatically when the browser is signed in. Use Swagger's **Authorize** action to supply either a JWT bearer token in OIDC or Hybrid mode, or an Agentstration personal access token. Agentstration does not issue OAuth tokens and Swagger does not add a token endpoint.

Uploads and streaming formats that cannot be inferred from handler signatures are described explicitly, including Pack ZIP archives, Pack downloads, and server-sent event streams. SignalR hubs and the MCP endpoint are separate transports and do not appear in OpenAPI.

Current API families include:

- Management resources under `/api/agents`, `/api/deployments`, and related short routes;
- Runtime Runs under `/api/runtime/runs`;
- Work Items under `/api/work/workitems`;
- Workplace routes under `/api/workspaces/{workspaceName}`;
- Flow definitions and Runs under `/api/flows` and `/api/flowRuns`;
- model providers/profiles and runtime profiles under `/api/...`.

Problem Details, ETags/conditional writes, pagination, and `202 Accepted` are used where their implemented boundary requires them. API versioning is explained in [Versioning strategy](../versioning.md).
