# Startup progress after the shared UX merge

Date: 2026-11-20. Branch: `feat/bootstrapping`. Incoming main: `51969396`
(merged PR #1238). Parent #1250 and deferred reusable journey #1251 remain open.
These captures record implementation smoke, not maintainer UX acceptance.

The Bootstrap page retains the existing MainLayout and its shared navigation,
context, theme and session controls. The provider repair link opens the new
resource details template. Provider creation uses the new wizard and fragment
sections; asynchronous discovery updates only cached observations and preserves
the form draft, EditContext and original ETag. Unavailable extension metadata
does not delay the saved provider overview or erase its stored bindings.

General shell and FlowCanvas lifecycle fixes and global responsive shell overrides
were withdrawn during scope review. The final change keeps Bootstrap-specific
shell slots and the shared UX from main. Captures and smoke observations below
record an earlier branch revision; they are not validation of the final PR head.

## Browser smoke

Real Release standalone Web host with Local authentication and the actual
SharedKeyFile Ollama extension, isolated temporary data and an offline upstream
catalog. The shipped development and Instance/Tenant/Workspace Ollama profiles
wait for exact model `qwen3:1.7b`; publishing that model releases Bootstrap and
automatically opens the Console on desktop FR and mobile EN. Direct navigation,
reload, light/dark themes and no document overflow passed. A second fixture with
a bounded model deadline keeps the failure screen, opens the authorized provider
repair page and signs out through the existing confirmation page. Anonymous
progress reads return 401; authenticated observations are `no-store`. No browser
page error was observed on the successful path. No credentials or raw logs are
included here.

The existing `model-administration.spec.ts` wizard/section-editing journey also
passed against managed local product hosts. This maintains existing coverage;
it does not start the deferred native reusable Bootstrap scenario #1251.

## Captures

- [Desktop FR, light](desktop-model-wait-fr.png)
- [Desktop FR, dark](desktop-model-wait-dark-fr.png)
- [Mobile EN, preparation steps](mobile-model-wait-en.png)
- [Mobile EN, dependency details](mobile-detail-en.png)
- [Desktop FR, terminal timeout](desktop-failed-fr.png)

## Decision numbering

Main published ADR-0238 for fragment navigation. The branch's automatic
enrollment decision moves to [ADR-0245](../../../../../decisions/0245-bootstrap-policies-assign-shared-key-extensions.md)
without a policy change; both decisions remain indexed independently.

The shared coverage inventories now declare `/startup/progress` as planned under
#1251 and classify StepProgress step navigation as a process control. The seven
route and collection inventory guards passed without weakening their assertions.
