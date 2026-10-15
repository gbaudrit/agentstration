using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.OpenApi;

public static class OpenApiDataSourceProfileBundle
{
    public static AepDataSourceProfileBundleContribution Create() => new(
        "openapi-governed", "Governed OpenAPI", "1.0.0", [ToolSet, DiscoveryFlow, AcquisitionFlow, Profile],
        "Discovers bounded OpenAPI operations and acquires responses only through explicitly promoted targets.",
        ["openapi.contract.discover", "openapi.operation.invoke"]);

    private const string ToolSet = """
        apiVersion: agentstration.io/v1
        kind: ToolSet
        metadata: { name: openapi-governed }
        definition:
          displayName: Governed OpenAPI
          description: Technical discovery and explicit invocation Tools supplied by the OpenAPI extension.
          version: 1.0.0
          publish: true
          members:
            - { tool: { binding: openapi.contract.discover }, capability: openapi.discover, route: default }
            - { tool: { binding: openapi.operation.invoke }, capability: openapi.invoke, route: default }
        """;

    private const string DiscoveryFlow = """
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: openapi-discovery }
        definition:
          displayName: Discover governed OpenAPI operations
          description: Discovers only configured same-origin contract candidates and returns bounded hierarchical choices.
          version: 1.0.2
          enabled: true
          metadata: { flow.contract: datasource.discovery/v1 }
          spec:
            flowKind: workflow
            entryNodeId: input
            nodes: [{ id: input, kind: input }]
            edges: []
          graph:
            entryStep: input
            inputSchema:
              type: object
              properties:
                discoveryId: { type: string }
                dataSourceId: { type: string }
                dataSourceUid: { type: string }
                dataSourceGeneration: { type: integer }
                profile: { type: object }
                sourceConfiguration: { type: object }
                parameters: { type: object }
                caller: { type: object }
                correlationId: { type: string }
              required: [discoveryId, dataSourceId, dataSourceUid, dataSourceGeneration, profile, sourceConfiguration, parameters, caller, correlationId]
              additionalProperties: false
            steps:
              - { type: input, name: input, displayName: Discovery request }
              - type: toolRoute
                name: discover
                displayName: Discover OpenAPI contracts
                toolSet: { resourceId: openapi-governed, version: 1.0.0 }
                capability: openapi.discover
                route: default
                argumentsMapping: { origin: "${input.sourceConfiguration.origin}", candidatePaths: "${input.sourceConfiguration.candidatePaths}", includeContracts: false, correlationId: "${input.correlationId}" }
              - type: output
                name: output
                displayName: Discovered operations
                outputMapping: { choices: "${steps.discover.output.structuredContent.choices}", artifacts: [], diagnostics: "${steps.discover.output.structuredContent.diagnostics}" }
              - { type: failure, name: failure, displayName: Discovery failed }
            transitions:
              - { id: input-discover, fromStep: input, event: completed, toStep: discover }
              - { id: discover-output, fromStep: discover, event: success, toStep: output }
              - { id: discover-failure, fromStep: discover, event: error, toStep: failure }
            outputSchema:
              type: object
              properties: { choices: { type: array, items: { type: object } }, artifacts: { type: array, items: { type: object } }, diagnostics: { type: array, items: { type: object } } }
              required: [choices, artifacts]
              additionalProperties: false
          publish: true
          activate: true
        """;

    private const string AcquisitionFlow = """
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: openapi-acquisition }
        definition:
          displayName: Acquire one selected OpenAPI operation
          description: Invokes only the exact promoted selector and persists its bounded response.
          version: 1.0.0
          enabled: true
          metadata: { flow.contract: datasource.acquisition/v1 }
          spec:
            flowKind: workflow
            entryNodeId: input
            nodes: [{ id: input, kind: input }]
            edges: []
          graph:
            entryStep: input
            inputSchema:
              type: object
              properties:
                acquisitionId: { type: string }
                dataSourceId: { type: string }
                dataSourceUid: { type: string }
                dataSourceGeneration: { type: integer }
                profile: { type: object }
                sourceConfiguration: { type: object }
                target: { type: object }
                parameters: { type: object }
                caller: { type: object }
                correlationId: { type: string }
              required: [acquisitionId, dataSourceId, dataSourceUid, dataSourceGeneration, profile, sourceConfiguration, target, parameters, caller, correlationId]
              additionalProperties: false
            steps:
              - { type: input, name: input, displayName: Acquisition request }
              - type: toolRoute
                name: invoke
                displayName: Invoke selected operation
                toolSet: { resourceId: openapi-governed, version: 1.0.0 }
                capability: openapi.invoke
                route: default
                argumentsMapping: { selector: "${input.target.selector}", arguments: "${input.parameters}", fixedBindings: "${input.sourceConfiguration.fixedBindings}", credentialId: "${input.sourceConfiguration.credentialId}", maximumPages: "${input.sourceConfiguration.maximumPages}", correlationId: "${input.correlationId}" }
              - type: toolRoute
                name: create
                displayName: Create staged response
                toolSet: { resourceId: staged-artifacts-builtin, version: 1.0.0 }
                capability: staged-artifact.create
                route: default
                argumentsMapping: { fileName: openapi-response.bin, mediaType: "${steps.invoke.output.structuredContent.mediaType}" }
              - type: toolRoute
                name: write
                displayName: Write response
                toolSet: { resourceId: staged-artifacts-builtin, version: 1.0.0 }
                capability: staged-artifact.write
                route: default
                argumentsMapping: { artifactId: "${steps.create.output.artifactReference}", offset: 0, contentBase64: "${steps.invoke.output.structuredContent.contentBase64}" }
              - type: toolRoute
                name: seal
                displayName: Seal response
                toolSet: { resourceId: staged-artifacts-builtin, version: 1.0.0 }
                capability: staged-artifact.seal
                route: default
                argumentsMapping: { artifactId: "${steps.create.output.artifactReference}" }
              - type: flow
                name: persist
                displayName: Persist acquired response
                flow: { resourceId: artifact-storage-filesystem-write-builtin, versionStrategy: active }
                inputMapping: { stagedArtifactId: "${steps.create.output.artifactReference}", producerFlowRunId: "${execution.flowRunId}", producerFlowStepId: seal }
              - type: output
                name: output
                displayName: Publish acquired response
                outputMapping:
                  artifacts:
                    - { artifactId: "${steps.persist.output.flowRunArtifactId}", kind: durable, disposition: publishable, name: "${input.dataSourceId}", mediaType: "${steps.invoke.output.structuredContent.mediaType}", digest: "${steps.invoke.output.structuredContent.sha256}" }
              - { type: failure, name: failure, displayName: Acquisition failed }
            transitions:
              - { id: input-invoke, fromStep: input, event: completed, toStep: invoke }
              - { id: invoke-create, fromStep: invoke, event: success, toStep: create }
              - { id: invoke-failure, fromStep: invoke, event: error, toStep: failure }
              - { id: create-write, fromStep: create, event: success, toStep: write }
              - { id: create-failure, fromStep: create, event: error, toStep: failure }
              - { id: write-seal, fromStep: write, event: success, toStep: seal }
              - { id: write-failure, fromStep: write, event: error, toStep: failure }
              - { id: seal-persist, fromStep: seal, event: success, toStep: persist }
              - { id: seal-failure, fromStep: seal, event: error, toStep: failure }
              - { id: persist-output, fromStep: persist, event: completed, toStep: output }
              - { id: persist-failure, fromStep: persist, event: error, toStep: failure }
            outputSchema:
              type: object
              properties: { artifacts: { type: array, items: { type: object } } }
              required: [artifacts]
              additionalProperties: false
          publish: true
          activate: true
        """;

    private const string Profile = """
        apiVersion: agentstration.io/v1
        kind: DataSourceProfile
        metadata: { name: openapi-governed }
        definition:
          displayName: Governed OpenAPI
          description: Discovers bounded contracts and acquires only explicitly promoted operations.
          enabled: true
          version: 1.0.0
          configurationSchema:
            type: object
            properties:
              origin: { type: string, format: uri, minLength: 8, maxLength: 2048 }
              candidatePaths: { type: array, minItems: 1, maxItems: 8, items: { type: string, maxLength: 512 } }
              credentialId: { type: string, maxLength: 128 }
              maximumPages: { type: integer, minimum: 1, maximum: 10 }
              fixedBindings: { type: object }
            required: [origin, candidatePaths, maximumPages]
            additionalProperties: false
          discoveryFlow: { name: openapi-discovery, version: 1.0.2, useActiveVersion: false }
          discoveryToolBindings:
            - { name: discover, capability: openapi.discover, tool: { binding: openapi.contract.discover } }
          acquisitionFlow: { name: openapi-acquisition, version: 1.0.0, useActiveVersion: false }
          toolBindings:
            - { name: invoke, capability: openapi.invoke, tool: { binding: openapi.operation.invoke } }
          limits: { maximumPages: 10, maximumResponseBytes: 4194304 }
          policies: { publicNetworkOnly: true, redirects: deny, explicitOperationSelection: true }
        """;
}
