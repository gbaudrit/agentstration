# Postman collection

`Agentstration.postman_collection.json` is the complete HTTP API collection. Its **API Reference** folder is generated from the same runtime OpenAPI document used by Swagger; its **Scenarios** folder is assembled from curated files under `scenarios/`.

Import the collection and one environment from `environments/`:

- `Agentstration.Local.postman_environment.json` targets the direct Web launch at `http://localhost:5100`.
- `Agentstration.Docker.postman_environment.json` targets the default provider Compose port at `http://localhost:5100`. Change `port` to `5080` for `deploy/compose/base.yml`, or to the value supplied through `AGENTSTRATION_HTTP_PORT`.

Every request resolves its address through `{{baseUrl}}`, whose value is `{{scheme}}://{{host}}:{{port}}`. Override only `scheme`, `host`, and `port` when moving between environments.

## Authentication

For local authentication, set `password` in the active Postman environment and run **Scenarios > 01 - Authentication > Login as local Platform administrator**. Postman's cookie jar sends the application session cookie on later requests.

For OIDC or automation, set `bearerToken` to a JWT or Agentstration personal access token. A collection pre-request script adds the Bearer header only when this variable has a value, so an empty token does not interfere with cookie authentication. Do not export populated credentials, tokens, or cookies into the repository.

## Generated reference

Run from the repository root:

```powershell
dotnet run --project tools/Agentstration.Postman
```

The command starts an in-memory `Testing` host, reads `/openapi/v1.json`, and deterministically regenerates the collection and environment templates. It requires no live model, provider, database server, or public network.

To verify committed artifacts without modifying them:

```powershell
dotnet run --project tools/Agentstration.Postman -- --check
```

Do not edit the generated **API Reference** section directly. Improve endpoint metadata or the generator instead. Curated workflows belong in `scenarios/`; regenerate the final collection after changing them.

## Request behavior

- Optional query parameters are present but disabled by default.
- Path and query values use collection variables and can be overridden by the active environment.
- Request bodies are representative examples generated from OpenAPI schemas; review identifiers and references before sending mutations.
- `DELETE` requests are marked as destructive in their descriptions and are never run automatically.
- Pack archive requests use Postman's file or multipart body modes; select the local ZIP before sending.
- Runtime and Flow event requests return `text/event-stream` and remain open while events are produced.
- The Source Registry scenario is deterministic except for the explicitly named official Registry refresh request, which can be skipped offline.

SignalR hubs and the MCP endpoint are separate protocols and are not represented as REST requests in this collection. Their HTTP negotiation routes are implementation details rather than part of the OpenAPI contract.
