# Runtime Plane

The Runtime Plane resolves governed definitions, materializes executable agents, invokes models and tools, and records technical executions. Durable Runtime Runs can exist independently of Work Items.

Provider-neutral runtime contracts are separated from the Microsoft Agent Framework adapter. Concrete framework types belong only in `Agentstration.Runtime.AgentFramework`.

The Runtime Plane does not own Agent desired state, Workplace conversation state, or Work Item lifecycle. See the [Runtime Plane implementation note](../runtime-plane.md).

Each Runtime Run carries an immutable execution scope derived by the API from the authenticated Agentstration Principal and selected Workspace. Queued execution reloads that scope, revalidates the Principal, Workspace, membership, and `runs/execute` permission, and only then resolves Management resources. Client payloads cannot provide the scope or initiator. Legacy Runs without a scope fail closed.
