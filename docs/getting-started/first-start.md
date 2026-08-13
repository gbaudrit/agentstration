# First start

With `Agentstration.Web` running, open `http://localhost:5100` and check `http://localhost:5100/health`. OpenAPI is available as `/openapi/v1.json` in the Development environment.

With the standalone Workplace pair running:

- Authoritative server and Console: `http://localhost:5100`
- Server health: `http://localhost:5100/health`
- Combined OpenAPI: `http://localhost:5100/openapi/v1.json` in Development
- Workplace: `http://localhost:5180`

First start creates local data under `.agentstration/` relative to the running host. These generated files are ignored by Git. The standalone control, work, runtime, and Flow data stores are logically separated.

For more executable examples, see [Current capabilities and detailed workflows](../reference/current-capabilities.md).
