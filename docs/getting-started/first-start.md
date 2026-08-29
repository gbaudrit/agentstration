# First start

With `Agentstration.Web` running, open `http://localhost:5100` and check `http://localhost:5100/health`. Swagger UI is available as `/swagger` and its OpenAPI document as `/openapi/v1.json` in the Development environment.

With the standalone Workplace pair running:

- Authoritative server and Console: `http://localhost:5100`
- Server health: `http://localhost:5100/health`
- Combined OpenAPI: `http://localhost:5100/openapi/v1.json` in Development
- Swagger UI: `http://localhost:5100/swagger` in Development
- Workplace: `http://localhost:5180`

First start creates local data under `.agentstration/` relative to the running host. These generated files are ignored by Git. The standalone control, work, runtime, and Flow data stores are logically separated.

The default Development profiles declaratively create the `admin / admin` fixture, Tenant `dev`, and Workspace `default`. A `NoBootstrap` profile leaves the instance empty and exposes `/bootstrap`, which asks for the first global administrator plus the initial Tenant and Workspace. Non-Development unattended deployments must supply an explicit bootstrap manifest path and referenced secret; base settings do not invent an initial topology.

For more executable examples, see [Current capabilities and detailed workflows](../reference/current-capabilities.md).
