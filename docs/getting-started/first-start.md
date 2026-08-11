# First start

With `Agentstration.Web` running, open `http://localhost:5100` and check `http://localhost:5100/health`. OpenAPI is available as `/openapi/v1.json` in the Development environment.

With the standalone Workplace pair running:

- Work API: `http://localhost:5080` (its root intentionally has no UI)
- Work API health: `http://localhost:5080/health`
- Work API OpenAPI: `http://localhost:5080/openapi/v1.json` in Development
- Workplace: `http://localhost:5180`

First start creates local data under `.agentstration/` relative to the running host. These generated files are ignored by Git. The standalone control, work, runtime, and Flow data stores are logically separated.

For more executable examples, see [Current capabilities and detailed workflows](../reference/current-capabilities.md).
