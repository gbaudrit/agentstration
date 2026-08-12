# Compatibility and versioning

AEP uses four independent version axes:

- protocol version, currently `2026-08-01`;
- SDK/package version, initially `0.1.0`;
- extension version, owned by each extension;
- consuming-product version, owned by Agentstration or another host.

Breaking wire changes require a new protocol version. Additive capability names do not. A capability may evolve independently through its descriptor version. Clients reject unsupported protocol versions but preserve unknown capability descriptors for forward-compatible inspection.

The packages intended for publication are `Agentstration.Aep.Abstractions`, `Agentstration.Aep.Client`, `Agentstration.Aep.AspNetCore`, and `Agentstration.Aep.Validation`. `Agentstration.Aep.MicrosoftExtensionsAI` is an optional integration package rather than part of the core protocol.
