# Agentstration browser automation

This workspace owns the Playwright page objects and journeys used by product UX tests and reproducible external capture.

```powershell
npm ci
npm run install:browsers
npm run test:smoke
```

To use Playwright UI against an already-running instance:

```powershell
$env:AGENTSTRATION_CONSOLE_URL = "http://localhost:53400"
$env:AGENTSTRATION_WORKPLACE_URL = "http://localhost:53401" # Optional for Console-only tests.
$env:AGENTSTRATION_USERNAME = "admin"
$env:AGENTSTRATION_PASSWORD = "admin"
$env:AGENTSTRATION_PLAYWRIGHT_CHANNEL = "chrome"             # Optional local fallback.
npx playwright test --ui
```

When `AGENTSTRATION_CONSOLE_URL` is present, the test fixture does not start or stop product hosts and requires the username/password pair. Journey input can explicitly override both values. Without an external URL, tests keep using dynamically allocated local ports, isolated SQLite data, and the public Development fixture `admin / admin`. An omitted Workplace URL falls back to the Console URL so Console-only journeys remain usable; set it explicitly before running a Workplace journey. External tests mutate the selected instance, so use a disposable instance or dedicated campaign Workspace.

To exercise the capture contract:

```powershell
npm run capture -- --plan examples/console-home.capture-plan.json --output .work/example-capture
```

The welcome-agent plan replays the first agent created in the handoff demo:

```powershell
npm run capture -- --plan examples/create-welcome-agent.capture-plan.json --output .work/welcome-agent
```

Create and select a dedicated campaign workspace with:

```powershell
npm run capture -- --plan examples/create-campaign-workspace.capture-plan.json --output .work/campaign-workspace
```

The solution-discovery video Flow and Entry are captured with:

```powershell
npm run capture -- --plan examples/create-solution-discovery-flow.capture-plan.json --output .work/solution-discovery-flow --console-url https://agentstration.example.com
npm run capture -- --plan examples/create-solution-discovery-entry.capture-plan.json --output .work/solution-discovery-entry --console-url https://agentstration.example.com
```

Run these commands against the same persistent instance and campaign Workspace. The Flow plan expects the four agent technical names declared in its `participants` input. The Entry plan expects that Flow to have been published and activated first.

To run that plan against an existing Console without starting local product hosts, override its URL from the command line:

```powershell
npm run capture -- --plan examples/create-welcome-agent.capture-plan.json --output .work/welcome-agent --console-url https://agentstration.example.com
```

Command-line URLs take precedence over plan values. `--workplace-url` is optional for Console-only journeys and can be supplied when a journey also uses Workplace.

On a persistent external instance, run the workspace plan first and set `workspaceName` in subsequent journey input. The target workspace must be prepared explicitly with every resource required by those journeys, such as model and runtime profiles. Local capture commands start isolated product state, so separate commands do not share a workspace.

Profile inputs may be an ordered array when equivalent environments use different resource names. The journey selects the first available candidate and reports the available options immediately when none match.

See [Browser automation](../../docs/contributing/browser-automation.md), [ADR-0079](../../docs/decisions/0079-product-owned-browser-journeys.md), and [ADR-0080](../../docs/decisions/0080-browser-campaigns-use-dedicated-workspaces.md) for ownership, extension, and external-consumption rules.
