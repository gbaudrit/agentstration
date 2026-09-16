# Reviewing Resource Plans in Console

Console exposes Resource Planning at `/resource-plans` in the current Workspace. Readers can open a durable plan and inspect five views:

- **Overview** shows the versioned functional intent, separate from concrete resources.
- **Changes** shows each ChangeSet operation and field-level Current versus Proposed differences.
- **Graph** shows the dependency order and explicit edges between proposed resources.
- **Validation** shows readiness and each issue's stable code and path.
- **Activity** shows the plan history and links to originating Work and Flow Run records when recorded.

The list and detail pages use the same Workspace-scoped `/api/resource-plans` endpoints as other clients. The current Workspace controls visibility. The page does not accept Tenant or Workspace IDs as request parameters. Users with `resources/read` may review; `resources/write` is required to re-materialize, create a ChangeSet, or revalidate it.

The review page marks a ChangeSet stale when its plan revision differs from the current plan. It marks validation stale when the ChangeSet digest or plan revision has changed. Re-materialization is non-mutating and displays diagnostics before the user creates a durable ChangeSet. Creating the same ChangeSet again is idempotent. No Console action applies a ChangeSet; governed application belongs to FR #330.

The UI is available in English and French and adapts to narrow screens. The graph includes an ordered text representation of dependencies for keyboard and assistive-technology users.

The deterministic browser journey lives in `tests/Agentstration.Console.Playwright`. After a Release build of the Web project, run `npm ci`, `npx playwright install chromium`, and `npm test` in that directory. It starts an isolated Development Web host on port 5199, signs in using the disposable `admin / admin` fixture, and exercises review through stale-state detection. Set `PLAYWRIGHT_CHANNEL=chrome` to use an installed Chrome browser for local runs.
