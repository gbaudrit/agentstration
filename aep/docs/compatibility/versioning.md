# Compatibility and versioning

AEP uses four independent version axes:

- protocol version, currently `2026-08-01`;
- SDK/package version, initially `0.1.0`;
- extension version, owned by each extension;
- consuming-product version, owned by Agentstration or another host.

Breaking wire changes require a new protocol version. Additive capability names do not. A capability may evolve independently through its descriptor version. Clients reject unsupported protocol versions but preserve unknown capability descriptors for forward-compatible inspection.

`aep.secret-requirements` version `1.0` is an additive discovery capability. An older extension omits the capability and declaration; consumers treat that as zero declared requirements. Changing the meaning or fields of a requirement needs a new capability version. Requirement identifiers remain stable contract names so future consumer bindings can refer to them without depending on installation-specific Secrets.

Configuration has an additional compatibility axis. An option-set version is immutable and its schema digest must remain stable. Extensions preserve existing consumers by continuing to publish and execute pinned versions when a newer version becomes preferred. Removing a published option-set version is a breaking change for consumers that still reference it and must be diagnosed before invocation.

The packages intended for publication are `Agentstration.Aep.Abstractions`, `Agentstration.Aep.Client`, `Agentstration.Aep.AspNetCore`, and `Agentstration.Aep.Validation`. `Agentstration.Aep.MicrosoftExtensionsAI` is an optional integration package rather than part of the core protocol.
