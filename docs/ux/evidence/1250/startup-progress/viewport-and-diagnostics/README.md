# Bootstrap viewport following and failure diagnostics

Implementation evidence captured on 2026-11-21 for #1250 and #871, in PR #1260.
This is not maintainer UX approval. Reusable browser scenarios #1251 and #1217
remain gated by explicit UX validation and implementation merge.

## Local executable and fixtures

The captures use the Release standalone Web executable, isolated SQLite files,
the disposable Development administrator and Chromium. No hosted AI service is
required. Only screenshots are committed; databases, shared keys and server logs
are excluded.

The initial-startup case runs the actual Ollama extension with SharedKeyFile
enrollment and a controllable offline upstream. The required model is absent.
The manual case uses deterministic AI, two Tenant profiles containing 512
distinct Parameters each and a Workspace profile whose first Data Source Profile
references an unavailable acquisition Flow. These are temporary local fixtures,
not new production Bootstrap profiles.

## Observed behavior

- Selected-profile preparation rows occupy 91 pixels each at 1600 × 900,
  including their target picker and ordering actions.
- Startup uses the standard Console frame, compact recap cards and dependency
  facts, without duplicating the navigation notice on the progress page.
- At 1600 × 900, the startup panes remain inside the viewport; their independent
  scrolling follows the selected group and current resource. On timeout, a safe
  `model_dependency_timeout` diagnostic remains above the scrolling panels.
- The manual application creates 1,024 Parameters, fails one Data Source Profile
  and leaves its second resource unstarted. Its native
  `data_source_acquisition_flow_unavailable` code is presented as an unavailable
  acquisition Flow, with a diagnostic reference matching the local server log.
- Current-resource following was observed while the application was running,
  across bounded 25-row pages. Scrolling pauses following and preserves the
  inspected group/page through completion. The existing follow action resumes
  on the failed group and reveals its diagnostic.
- Reloading retains the same application, one request identity and the same
  failure reference. It does not submit another application.
- Both progress views expose expandable resource details. Failure context also
  remains visible above the panes when another group is selected.
- French desktop, French startup mobile and English manual mobile were checked.
  Manual mobile captures cover light and dark themes. No document-level
  horizontal overflow was observed at 390 pixels; the manual screen also passed
  at 320 pixels. No JavaScript errors were observed.

A separate temporary browser check exercised the actual shared JavaScript
module with controlled scroll containers, including delayed arrival of the
current resource row, manual pause, explicit resume and cleanup. This supplements
the local product checks; it is not a delivered reusable browser journey.

## Captures

- [Compact preparation](preparation-fr.png)
- [Manual application running](application-running-fr.png)
- [Manual application failed, with diagnostic](application-failed-fr.png)
- [The same application after reload](application-reloaded-fr.png)
- [Manual application, mobile light](application-mobile-light.png)
- [Manual application, mobile dark](application-mobile-dark.png)
- [Initial startup waiting](startup-initial-fr.png)
- [Initial startup failed](startup-failed-fr.png)
- [Initial startup failure, mobile](startup-failed-mobile-fr.png)

## Limits

Initial-startup observations remain process-local. Manual application diagnostics
are retained with their existing durable application records. Older records
without a captured cause explicitly report that the precise cause was not
recorded; it cannot be reconstructed retroactively. Only allowlisted categories,
bounded technical codes and generated references cross the diagnostic boundary;
arbitrary exception messages, provider responses and secret values do not.

The full repository browser campaign is not certified by these focused checks.
Release, Fast, Integration, component and documentation results are recorded in
the PR's Validation section.
