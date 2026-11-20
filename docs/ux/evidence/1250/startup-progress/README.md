# Authenticated startup progress — implementation smoke

Date: 2026-11-20. Branch: `feat/bootstrapping`. Parent: #1250; deferred reusable
journey: #1251. This records browser smoke for the executable implementation, not
maintainer UX validation or a cataloged Playwright journey.

## Fixture

Real Release standalone Web host in Local authentication mode, isolated temporary
SQLite data and SharedKeyFile enrollment, plus the actual Ollama AEP extension.
The shipped development/ollama-instance/ollama-tenant/ollama-workspace profiles
exercise Instance, Tenant and Workspace bootstrap. An offline HTTP fixture serves
an initially empty model catalog, then the exact `qwen3:1.7b` model. No live model,
weights, cloud account, Docker or inference is involved. Generated credentials and
shared keys stay in temporary fixture storage and are not included here.

Chromium, 1440×1000/fr-FR and 390×844/en-US, light/dark themes and reduced motion.
The updated captures use the existing Console MainLayout, not a separate application frame.

## Observations

- Before full readiness, the empty login-availability probe reaches 204 while
  readiness stays 503. Anonymous progress API access is 401.
- Administrator login from `/` redirects to `/startup/progress?ReturnUrl=%2F`;
  the ordinary overview is not rendered while initialization is pending.
- A delayed extension appears after login. The shared component moves from
  required contributions to resource/dependency application without a reload.
- One generic panel displays provider/model identities supplied by the snapshot.
  The real exact-model wait remains responsive; no provider-specific panel exists.
- Reload reconnects to the same attempt. Progress responses include `no-store`.
- Adding the exact model to the offline upstream completes bootstrap. Both open
  desktop and mobile pages automatically reach `/` and leave StepProgress.
- A separate fixture with a bounded model deadline retains the failed progress
  screen, expected model/provider facts, failed-resource count and correction/
  restart guidance. Readiness remains 503.
- The authorized provider-configuration repair link opens the actual provider
  page even after failure. The logout link opens the existing confirmation page;
  submitting it returns to login.
- The existing Console sidebar, context selector, theme and session actions remain
  present. A custom status slot prevents operational dashboard reads during startup;
  the command palette is temporarily disabled. Direct HTML navigation to an Agent
  route returns to progress, while API execution remains fenced.
- Native stage progress, shared status badges and metric cards present real phase
  completion and outcomes; no global work-completion percentage is invented.
- Light/dark preference changes work while pending. Mobile cards stack without
  document overflow; the menu and context selector do not overlap, and the sidebar
  health footer stays hidden in the mobile navigation bar. The root transition
  also works with the standard Console context-preservation handler.
- No browser page errors were observed during the success path.

Network read failures/timeouts and subsequent recovery are covered by component
regressions with a controlled clock. Real HTTP security regressions separately
cover non-administrator sign-in, retained sessions, BFF validation, protected
repair APIs and restoration of ordinary admission after readiness. OIDC admission
is exercised through the configured ticket callback without a live identity
provider.

The existing cataloged `console-authentication.spec.ts` administrator-login smoke
also passed against its managed local hosts (desktop Chromium), confirming ordinary
entry after readiness. This reuses an existing journey; it does not implement the
new deferred #1251 scenario.

## Captures

- [Delayed contribution, desktop FR](desktop-contributions-fr.png)
- [Model dependency, desktop FR](desktop-model-wait-fr.png)
- [Model dependency, dark desktop FR](desktop-model-wait-dark-fr.png)
- [Preparation steps, mobile EN](mobile-model-wait-en.png)
- [Model dependency details, mobile EN](mobile-detail-en.png)
- [Terminal model timeout, desktop FR](desktop-failed-fr.png)

The first delivery observes the current process-local startup attempt. It does
not persist a startup journal, add a new worker-readiness barrier or retry a
terminal failed bootstrap from the UI. The broader #1250 follow-ups and reusable
journey #1251 remain open.
