# Runtime plane

The runtime plane materializes and invokes agents from immutable revisions. It never becomes authoritative for definitions or versions.

`Agentstration.Runtime.Abstractions` defines runtime factories, instances, provisioners, observations, the runtime registry, and the single-agent router. `Agentstration.Runtime.AgentFramework` is the only project that constructs and holds Microsoft Agent Framework `AIAgent` objects. `Agentstration.Runtime.Local` supplies in-process/shared-host provisioning, registry, observation, and reconciliation.

The standalone factory currently supports the `prompt-agent` handler. Interfaces and explicit unsupported provisioners reserve dedicated process, dedicated container, remote endpoint, and Foundry hosting without pretending those modes are implemented.
