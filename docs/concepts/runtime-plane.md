# Runtime Plane

The Runtime Plane resolves governed definitions, materializes executable agents, invokes models and tools, and records technical executions. Durable Runtime Runs can exist independently of Work Items.

Provider-neutral runtime contracts are separated from the Microsoft Agent Framework adapter. Concrete framework types belong only in `Agentstration.Runtime.AgentFramework`.

The Runtime Plane does not own Agent desired state, Workplace conversation state, or Work Item lifecycle. See the [Runtime Plane implementation note](../runtime-plane.md).
