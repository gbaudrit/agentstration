# Agentstration browser automation

This workspace owns the Playwright page objects and journeys used by product UX tests and reproducible external capture.

```powershell
npm ci
npm run install:browsers
npm run test:smoke
```

To exercise the capture contract:

```powershell
npm run capture -- --plan examples/console-home.capture-plan.json --output .work/example-capture
```

The welcome-agent plan replays the first agent created in the handoff demo:

```powershell
npm run capture -- --plan examples/create-welcome-agent.capture-plan.json --output .work/welcome-agent
```

See [Browser automation](../../docs/contributing/browser-automation.md) and [ADR-0079](../../docs/decisions/0079-product-owned-browser-journeys.md) for ownership, extension, and external-consumption rules.
