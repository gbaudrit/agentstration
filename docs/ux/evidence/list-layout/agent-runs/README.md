# Agent Runs list composition

Real deterministic product-host fixture with an enrolled Runtime Worker, local fake Ollama and isolated SQLite. The existing Worker observability fixture prepares a Workspace Agent/model profile; 25 real API-created Runs complete successfully with public synthetic prompts. No paid provider or real user content is used. Fixture preparation reuses existing support and does not implement the deferred #1242 journey.

[Desktop English, 1440×1000](desktop-en.png), [mobile French controls, 390×844](mobile-fr.png), [mobile result card](mobile-fr-results.png), [source proof](source-proof.json).

Both browser checks pass: real 20 + 5 server pages, previous/next, unknown total without direct jumps or page-local search/filter controls, refresh/view retention, exact execution-scope/navigation links, Agent/outcome rendering and no redundant Open-only menu or document horizontal overflow. Six targeted EN/FR cases pass, covering one locally constructed API request per page without following nextLink, size changes, retained successful facts on failed page and actual retry, useful Previous on an empty terminal window, late response/context replacement, runs/read denial, and duration/outcome/start-time projection. Initial test expectations used a padded hour while the existing `TimeSpan` general format is unpadded; corrected expectations pass.

Web/Workplace Release builds have no warnings; full targeted format and two catalog assertions pass. Known incoming TransformationFunctionEditor inventory omission not redundantly rerun. Full functional lanes and new reusable #1242 journey remain deferred.
