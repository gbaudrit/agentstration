# Workers list composition

Real deterministic product-host fixture, including Web, Workplace and a live enrolled AWP Runtime Worker with a real session. No Worker mutation, paid provider or Agent run is used. One actual Worker is present; multi-page catalog semantics are exercised by the component/HTTP cases rather than claimed from a one-Worker browser run.

[Desktop English, 1440×1000](desktop-en.png), [mobile French controls, 390×844](mobile-fr.png), [mobile result card](mobile-fr-results.png), [source proof](source-proof.json).

Both browser checks pass: actual Worker UUID search; presence/enrollment/capability/session/software-version filters; no-match reset; refresh/view retention; exact context-preserving Worker link; presence versus enrollment, software versus build, capacity versus active assignments, capability/material/implementation versions; no redundant Open-only action or document horizontal overflow. Visual inspection found that legacy cell formatting depended on removed local grid CSS and concatenated capacity/active load. Semantic line breaks fix the cell; the obsolete local responsive CSS is deleted, and both browser views were rerun after aligned builds.

Six new EN/FR behavior cases plus the maintained Worker inventory regression pass. Cases cover 45-row pagination, full-array filtering, refresh failure/retry/shrink, stale/context-replaced responses, initial API denial and same-identity reauthorization clearing privileged rows/options. The existing PlatformAdmin API policy remains authoritative. Web/Workplace Release builds have no warnings; full targeted format and two catalog assertions pass. Known incoming TransformationFunctionEditor inventory omission not redundantly rerun. Full functional suites and new reusable #1242 journey remain deferred.

Incoming `7f03af96` changes Flow Run details and tests, without changing Worker or collection code; no extra Worker behavior retest was justified.
