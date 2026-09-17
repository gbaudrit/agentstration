# Reviewing Resource Plans in Console

Console exposes Resource Planning at `/resource-plans` in the current Workspace. Readers can open a durable plan and inspect six views:

- **Overview** shows the versioned functional intent, separate from concrete resources.
- **Changes** shows each ChangeSet operation and field-level Current versus Proposed differences.
- **Graph** shows the dependency order and explicit edges between proposed resources.
- **Validation** shows readiness and each issue's stable code and path.
- **Application** shows the governed apply action, attempt status, and per-operation outcomes.
- **Activity** shows the plan history and links to originating Work and Flow Run records when recorded.

The list and detail pages use the same Workspace-scoped `/api/resource-plans` endpoints as other clients. The current Workspace controls visibility. The page does not accept Tenant or Workspace IDs as request parameters. Users with `resources/read` may review; `resources/write` is required to re-materialize, create a ChangeSet, verify it, or apply it.

The operator's Model Profile, Runtime Profile, and integration Tool choices are saved with the plan revision and restored when the page is reopened. An integration Tool is assigned to the planned agents whose roles depend on that integration. The review page marks a ChangeSet stale when its plan revision differs from the current plan. It marks validation stale when the ChangeSet digest or plan revision has changed. Re-materialization is non-mutating and displays diagnostics before the user creates a durable ChangeSet. Creating the same ChangeSet again is idempotent. After a successful verification, **Apply proposal** submits the exact plan revision, ChangeSet digest, and verification ID. The result lists each operation and any failure. A partial application can be resumed; completed operations are not repeated.

Application stops at the first failed operation and does not roll back successful operations. The API records the application attempt and individual outcomes in the current Workspace. A second submission of a completed application returns its recorded result. Write and delete permissions, resource scope, profile and Tool bindings, and current resource versions are checked when each operation executes.

The UI is available in English and French and adapts to narrow screens. The graph includes an ordered text representation of dependencies for keyboard and assistive-technology users.

The deterministic browser journey lives in `tests/Agentstration.Console.Playwright`. After a Release build of the Web project, run `npm ci`, `npx playwright install chromium`, and `npm test` in that directory. It starts an isolated Development Web host on port 5199, signs in using the disposable `admin / admin` fixture, and exercises review through stale-state detection. Set `PLAYWRIGHT_CHANNEL=chrome` to use an installed Chrome browser for local runs.
