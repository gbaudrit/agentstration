# Secrets list composition

Real deterministic Development Web/API with isolated SQLite. Twenty-five Secret metadata resources reference two local Vaults. No Secret value read/write or Vault initialization occurred.

[Desktop English, 1440×1000](desktop-en.png), [mobile French controls, 390×844](mobile-fr.png), [mobile result card](mobile-fr-results.png), [source proof](source-proof.json).

Both browser checks pass: whole-array name search, 20 + 5 paging, Vault/type/scope filtering, no-match reset, refresh/view retention, exact name/scope links, legitimate Secrets/Vaults navigation and no redundant Manage-only menu or document horizontal overflow. Five targeted EN/FR cases pass, including status filtering, refresh failure/retry/shrink, context replacement, late responses and permission gates. Web Release build, full targeted dotnet format and two catalog assertions pass. The known incoming TransformationFunctionEditor inventory omission was not redundantly rerun. Full functional lanes and new reusable #1242 journey remain deferred.

Incoming `43183fd2` changes durable Artifact details and tests; it does not affect this list or the shared collection template. No additional Secrets behavior retest was justified.
