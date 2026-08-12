# Concepts

Agentstration separates what is declared, what executes, and what users ask the system to accomplish.

| Distinction | Meaning |
| --- | --- |
| Agent ≠ Agent Type | An Agent is a governed definition; an Agent Type is a reusable behavioral template referenced by an Agent. |
| Model Provider ≠ Model Profile | A Provider describes connectivity/capabilities; a Profile expresses portable model intent and selects a provider/model. |
| Tool Provider ≠ Tool | A Tool Provider is a discoverable source; a Tool is Agentstration's governed identity with independent enablement and availability. |
| Flow ≠ FlowRun | A Flow is a versioned processing definition; a FlowRun is one durable execution of a resolved definition. |
| Interaction ≠ WorkTask | An Interaction is the durable user conversation; a WorkTask is the user-facing projection of asynchronous work. |
| Management Plane ≠ Runtime Plane | Management owns desired state; Runtime materializes and executes it. |
| Tenant ≠ Workspace | Tenant is a planned broader isolation concept; Workspace is the implemented Workplace isolation boundary. |

```mermaid
flowchart TD
    Interaction -->|may create| WorkTask
    WorkTask --> WorkItem
    WorkItem -->|references| Flow
    Flow -->|instantiated as| FlowRun
    FlowRun --> Runtime
    Runtime --> Agent
    Agent --> AgentType[Agent Type]
    Agent --> ModelProfile[Model Profile]
    Agent --> Tool
    Tool --> ToolProvider[Tool Provider]
    ModelProfile --> ModelProvider[Model Provider]
```
