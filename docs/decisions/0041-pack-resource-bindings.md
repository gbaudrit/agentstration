# ADR-0041 — Pack resource bindings are logical and installation-scoped

Status: Accepted

## Context

Pack resources may require workspace-specific Model Profiles or Secrets. Embedding those concrete references in an archive makes a Pack environment-specific, while copying Secret values into a Pack would violate the Vault boundary established by ADR-0040. Reinstallation must not discard the operator's local choices.

## Decision

- A Pack may declare named logical `bindings` in its `definition`. V1 supports targets of kind `modelProfile` and `secret`.
- A contained resource references a declaration with an object of the form `{ binding: logical-name }`. Installation resolves that placeholder to a normal explicit `ResourceReference`; resource owners continue to receive and validate their native manifests.
- Binding selections are workspace-scoped and keyed by the stable Pack identity `publisher/name`, independently of the Pack version and installed archive.
- The Management Plane stores only the selected target namespace and name. Secret values remain write-only Vault data and never enter Pack manifests, previews, installation records, logs, or browser state.
- A successful installation snapshots its effective resolutions in `InstalledPack` for inspection and updates the durable Pack configuration. Uninstall preserves the configuration, so reinstalling another version of the same Pack identity reuses available targets.
- A fork preserves logical binding declarations but receives a new Pack identity and therefore does not inherit local selections implicitly.
- Missing required targets block installation. A previously selected target that no longer exists remains visible as unavailable until the operator chooses a replacement.

## Consequences

Packs remain portable and immutable while installations can select local inference and credential resources. Model and Secret selection share one extensible mechanism without exposing sensitive values. Future binding kinds may be added only when their resource ownership and compatibility validation are executable end to end.
