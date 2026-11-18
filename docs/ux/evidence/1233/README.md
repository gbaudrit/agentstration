# ResourceNavigation component evidence (#1233)

These are screenshots of the real shared Razor component running in a temporary, isolated Interactive Server host. The specimen is explicitly labelled and contains no Agent data. Its surrounding grid is test-host composition, not the deferred production resource-detail layout or an Agent pilot.

Product sources: commit `6f33d294a0475633869a53429175af053ab0b17f`, based on main `52c4b1dae9b0bcf98088669250934be7000542e9`. Source/image SHA-256 values and Chromium version are recorded in [provenance.json](provenance.json).

| Variant | Component | Drawer |
| --- | --- | --- |
| English, dark, desktop 1440×1000 | [Capture](en-US-dark-component.png) | — |
| French, light, desktop 1440×1000 | [Capture](fr-FR-light-component.png) | — |
| English, light, touch 390×844 | [Capture](en-US-light-component.png) | [Capture](en-US-light-drawer.png) |
| French, dark, touch 390×844 | [Capture](fr-FR-dark-component.png) | [Capture](fr-FR-dark-drawer.png) |

[smoke-results.json](smoke-results.json) records four passing variants. The executed script checks direct subsection links with obsolete selectors ignored/removed, reload/new-tab restoration, unsaved form and invalid YAML retention through generated navigation and guarded Back/Forward, programmatic fragment changes through the public Blazor API, unchanged resource/context coordinates, hidden-section fallback, long labels and no horizontal document overflow. Mobile checks use real touch activation, selection/outside/Escape dismissal, focus containment/return and responsive focus transfer. It also asserts theme token application and no console/page errors.

## Known failing integration probe

[native-hash-probe.json](native-hash-probe.json) deliberately records a failure, not a passing smoke result. Direct assignment to `window.location.hash`, followed by Back, passes with the leave guard absent and fails with the existing `UnsavedNavigationGuard` protecting dirty inputs. The probe exits nonzero if either case fails. The component uses the public Blazor navigation API for its own links and does not patch Blazor's internal history indices or suppress its leave guard. Resolve this combined native-history/guard case during the actual layout/pilot integration before completing the parent FR's draft/hash criterion.

This component increment does not complete #1233's production layout/pilot criteria and does not unlock the deferred reusable scenario #1234. External programmatic fragment observation uses the optional browser Navigation API; verify the supported browser matrix on adoption.

## Reproduce outside the product routes

Install the repository's .NET SDK and `automation/playwright` npm dependencies. The local capture used `/usr/bin/chromium`; adjust that executable in the copied scripts for another installation. Keep builds finished before running the browser campaign.

Create the temporary harness and scripts from the archived exact sources, replacing only the checkout path:

```bash
python3 - /absolute/path/to/agentstration /tmp/resource-navigation-host <<'PY'
import json, pathlib, sys
repo, target = map(pathlib.Path, sys.argv[1:])
evidence = repo / 'docs/ux/evidence/1233'
for name, content in json.loads((evidence / 'isolated-host-source.json').read_text()).items():
    output = target / name
    output.parent.mkdir(parents=True, exist_ok=True)
    output.write_text(content.replace('/workspace/agentstration-1233', str(repo)))
for name in ['component-smoke', 'native-hash-probe']:
    (target / (name + '.cjs')).write_text((evidence / (name + '.cjs.txt')).read_text().replace('/workspace/agentstration-1233', str(repo)))
PY
dotnet build /tmp/resource-navigation-host/NavigationHarness.csproj --configuration Release
ASPNETCORE_ENVIRONMENT=Development dotnet /tmp/resource-navigation-host/bin/Release/net10.0/NavigationHarness.dll --urls http://127.0.0.1:53133
```

In a second terminal, run `node /tmp/resource-navigation-host/component-smoke.cjs`. Run `node /tmp/resource-navigation-host/native-hash-probe.cjs` separately to reproduce the documented nonzero result. These scripts write their reports/captures to this evidence directory; preserve committed evidence before rerunning. The temporary fixture and scripts are not a product route or the reusable journey gated by #1234.
