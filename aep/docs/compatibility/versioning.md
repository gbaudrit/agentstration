# Compatibility and versioning

AEP uses four independent version axes:

- protocol version, currently `agentstration.io/v1`;
- SDK/package version, initially `0.1.0`;
- extension version, owned by each extension;
- consuming-product version, owned by Agentstration or another host.

The protocol identifier is a stable group/schema version, not a release date. Only `agentstration.io/v1` is accepted and emitted. Dated identifiers are unsupported; no fallback negotiation, aliases, legacy-document migration or transitional acceptance is provided because no prior contract has shipped. Development peers must be rebuilt together and disposable development Runtime data recreated if it contains dated versions. AEP owns this constant independently and does not reference Agentstration resource assemblies.

Breaking wire changes require a new protocol version. Additive capability names do not. A capability may evolve independently through its descriptor version. Clients reject unsupported protocol versions but preserve unknown capability descriptors for forward-compatible inspection.

`aep.value-requirements` and `aep.bound-values` version `1.0` are part of protocol `agentstration.io/v1`. They replace the former extension-wide Secret requirement declaration and chat-only Secret access field. Requirements are scoped to one contribution and retain stable logical identifiers independent of installation Parameters or Secrets. There is no legacy wire compatibility path.

Protocol `agentstration.io/v1` replaces model-level string capability lists and open metadata with the typed, partial model observation carried by `aep.model-provider`. The capability remains version `1.0` while AEP is pre-stable, and no legacy model descriptor wire shape is retained.

Configuration has an additional compatibility axis. An option-set version is immutable and its schema digest must remain stable. Extensions preserve existing consumers by continuing to publish and execute pinned versions when a newer version becomes preferred. Removing a published option-set version is a breaking change for consumers that still reference it and must be diagnosed before invocation.

The packages intended for publication are `Agentstration.Aep.Abstractions`, `Agentstration.Aep.Client`, `Agentstration.Aep.AspNetCore`, and `Agentstration.Aep.Validation`. `Agentstration.Aep.MicrosoftExtensionsAI` is an optional integration package rather than part of the core protocol.
