# ADR-0037: Packs are Management and distribution artifacts

Status: Accepted — 2026-08-13

## Context

Agentstration resources are authored and versioned by their owning modules, while Runtime, Flow, and Work each own distinct execution state. Distributing a useful capability often requires several resources, such as an Agent, a Flow, and a Workplace Entry. Treating that bundle as another execution primitive would duplicate the existing lifecycle and blur ownership between the planes.

Installation also needs information that no individual resource owns: publisher and package identity, package version, source, dependency requirements, presentation metadata, and the exact set of installed resources. That information is required for discovery, configuration, update, and safe uninstall.

## Decision

An Agentstration **Pack** is a versioned distribution and installation artifact containing zero or more ordinary Agentstration resource manifests. It belongs to the Management/distribution plane. A Pack is never executed; after installation, Runtime, Flow, and Work consume the installed resources through their existing contracts.

A Pack has a stable identity independent of its version. The conceptual coordinate is `publisher/name/version`, with Semantic Versioning as the initial package-version policy. Pack versioning describes a coherent set and does not replace the schema version, generation, ETag, or immutable version owned by a contained resource.

ADR-0035 scopes resource names through generic namespaces. A Pack installation uses the deterministic namespace `publisher.name`; publisher and Pack name therefore use lowercase ASCII letters, digits and `-`. Contained resource names remain unchanged, relative references resolve inside that namespace, and explicitly qualified references may target shared resources such as those in `default`. The namespace is a naming partition, while provenance continues to record Pack ownership independently.

The Pack manifest describes presentation metadata, contained resource paths, and requirements. Each contained file remains a normal resource manifest identified by its own `apiVersion` and `kind`; the Pack importer dispatches it to the owning module rather than interpreting its implementation. The Pack manifest is a distribution envelope, not another runtime-capable Management resource.

An installation records an `InstalledPack` management record and per-resource provenance. Provenance includes, at minimum, Pack identity, installed Pack version, resource kind and canonical name, and the installed content fingerprint or equivalent revision evidence. It must be sufficient to distinguish a still-managed resource from one that was changed or detached locally.

Installation must validate the complete archive, paths, manifest, requirements, and resources before applying changes. Applying resources and recording ownership must either complete as one explicit operation or leave a durable failed operation that can be safely compensated or resumed; an unrecorded partial installation is forbidden. Uninstall removes only resources that are still owned by the Pack and must check dependencies and live references first.

Pack configuration is installation-wide configuration. Functional instances and their input remain Work-owned concepts such as an Automation or Task. Installing one Pack may therefore enable any number of Work instances without creating a `PackInstance`.

The initial executable scope is deliberately limited to local/offline archives, manifest validation, installation, provenance, listing installed Packs, uninstall, and Pack version. Advanced dependency resolution, update conflict policy, signatures, verified publishers, remote Gallery access, and Marketplace concerns are later increments. A Gallery is merely one possible discovery and artifact source and is not part of the Pack format.

## Consequences

- No `RunPack`, `PackTask`, `PackInstance`, Pack runtime, or Pack-specific router path is introduced.
- Routers discover installed Agents, Flows, Entries, Tools, Model Profiles, and other resources without knowing which Pack supplied them.
- A source Pack and a fork with a different Pack identity can coexist because they install homonymous resources into different namespaces.
- Pack installation must coordinate resource owners; it cannot bypass their validation or write directly into their stores.
- Local archives and private sources remain first-class, so a remote Gallery is optional.
- Publisher trust, signatures, permissions, path traversal, archive expansion, and sensitive dependency configuration are explicit security boundaries.
- The concrete wire format remains pre-contractual until the importer and cross-module installation operation are implemented and tested together.
