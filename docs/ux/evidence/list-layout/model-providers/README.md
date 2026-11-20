# Model Providers list composition

Real deterministic product hosts from the existing browser fixture: Console/API/SQLite and local AEP Ollama extension using the fake local Ollama service, without a live LLM. Twenty-five ordinary Model Providers were created through the API in Instance context across `qa-a`/`qa-b`; no endpoint or remote provider is fabricated by the page adapter.

[Desktop English, 1440×1000](desktop-en.png), [mobile French controls, 390×844](mobile-fr.png), [mobile result card](mobile-fr-results.png), [source proof](source-proof.json).

Both browser checks pass: whole-response name search, 20 + 5 result paging, status/type/namespace filters, no-match reset, refresh/view retention, exact owner-qualified links, no redundant View details menu or document horizontal overflow. Five targeted EN/FR component cases pass, including failed refresh/retry, snapshot shrink, stale/context-replaced responses and permission-gated creation. Web/Workplace Release builds pass without warnings; full targeted dotnet format passes.

Two global catalog assertions pass; source-to-manifest equality still reports the incoming TransformationFunctionEditor ConfigurationEntry omission previously established on b5bb04fc. The Model Providers frame itself is reconciled. Incoming dff24428 changes Task FlowRun details/tests only. Full functional lanes and complete browser journeys were not rerun; #1242 remains blocked. These ad hoc captures do not implement that reusable journey.
