# System context

Users work through Workplace; operators govern resources and supervise execution through Console; external systems use HTTP or MCP. Agentstration persists declarations and functional state locally, then calls an optional model provider during execution.

```mermaid
flowchart TB
    User[End user] --> Workplace[Agentstration Workplace]
    Operator[Operator or developer] --> Console[Agentstration Console]
    External[External system] --> APIs[REST / MCP APIs]
    Workplace --> Platform[Agentstration]
    Console --> Platform
    APIs --> Platform
    Platform --> Local[(Local persistence)]
    Platform --> Provider[Optional model provider]
```

Azure, Foundry, Ollama, Docker, and remote credentials are not mandatory system dependencies. The deterministic provider supports offline execution.
