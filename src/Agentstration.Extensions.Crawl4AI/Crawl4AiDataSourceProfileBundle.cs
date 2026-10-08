using Agentstration.Aep.Abstractions;

namespace Agentstration.Extensions.Crawl4AI;

public static class Crawl4AiDataSourceProfileBundle
{
    public static AepDataSourceProfileBundleContribution Create() => new(
        "crawl4ai-web",
        "Crawl4AI Web",
        "1.0.0",
        [ToolSet, TransferFlow, AcquisitionFlow, Profile],
        "Acquires any allowed Web origin through Crawl4AI and publishes one governed durable corpus Artifact.",
        ["web.crawl", "content.read", "content.delete"]);

    private const string ToolSet = """
        apiVersion: agentstration.io/v1
        kind: ToolSet
        metadata:
          name: crawl4ai-web-acquisition
          tags: { agentstration.io/category: data-source }
        definition:
          displayName: Crawl4AI Web acquisition
          description: Exact Crawl4AI Tools used by the reusable Web Data Source Profile.
          version: 1.0.0
          publish: true
          members:
            - { tool: { binding: web.crawl }, capability: web.crawl, route: default }
            - { tool: { binding: content.read }, capability: content.read, route: default }
            - { tool: { binding: content.delete }, capability: content.delete, route: default }
        """;

    private const string TransferFlow = """
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: crawl4ai-web-transfer-chunk }
        definition:
          displayName: Transfer one Crawl4AI content chunk
          description: Reads one bounded Crawl4AI chunk and appends it to governed staging.
          version: 1.0.0
          enabled: true
          spec: { flowKind: direct, target: { kind: agent, id: unused-graph-target } }
          graph:
            entryStep: input
            inputSchema:
              type: object
              properties: { contentReference: { type: string }, stagedArtifactId: { type: string }, offset: { type: integer, minimum: 0 } }
              required: [contentReference, stagedArtifactId, offset]
              additionalProperties: false
            steps:
              - { type: input, name: input, displayName: Chunk request }
              - type: toolRoute
                name: read
                displayName: Read Crawl4AI content
                toolSet: { resourceId: crawl4ai-web-acquisition, version: 1.0.0 }
                capability: content.read
                route: default
                argumentsMapping: { contentReference: "${input.contentReference}", offset: "${input.offset}" }
              - type: toolRoute
                name: write
                displayName: Write staged content
                toolSet: { resourceId: staged-artifacts-builtin, version: 1.0.0 }
                capability: staged-artifact.write
                route: default
                argumentsMapping: { artifactId: "${input.stagedArtifactId}", offset: "${steps.read.output.structuredContent.offset}", contentBase64: "${steps.read.output.structuredContent.contentBase64}" }
              - type: output
                name: output
                displayName: Chunk progress
                outputMapping: { contentReference: "${input.contentReference}", stagedArtifactId: "${input.stagedArtifactId}", nextOffset: "${steps.read.output.structuredContent.nextOffset}", endOfContent: "${steps.read.output.structuredContent.endOfContent}" }
            transitions:
              - { id: input-read, fromStep: input, event: completed, toStep: read }
              - { id: read-write, fromStep: read, event: completed, toStep: write }
              - { id: write-output, fromStep: write, event: completed, toStep: output }
            outputSchema:
              type: object
              properties: { contentReference: { type: string }, stagedArtifactId: { type: string }, nextOffset: { type: integer }, endOfContent: { type: boolean } }
              required: [contentReference, stagedArtifactId, nextOffset, endOfContent]
              additionalProperties: false
          publish: true
          activate: true
        """;

    private const string AcquisitionFlow = """
        apiVersion: agentstration.io/v1
        kind: Flow
        metadata: { name: crawl4ai-web-acquisition }
        definition:
          displayName: Acquire a Web origin with Crawl4AI
          description: Crawls one configured Web origin and persists one bounded normalized corpus.
          version: 1.0.0
          enabled: true
          metadata: { flow.contract: datasource.acquisition/v1 }
          spec: { flowKind: direct, target: { kind: agent, id: unused-graph-target } }
          graph:
            entryStep: input
            inputSchema:
              type: object
              properties:
                dataSourceId: { type: string }
                dataSourceUid: { type: string }
                dataSourceGeneration: { type: integer }
                profile: { type: object }
                sourceConfiguration: { type: object }
                parameters: { type: object }
                caller: { type: object }
                correlationId: { type: string }
                acquisitionId: { type: string }
              required: [dataSourceId, dataSourceUid, dataSourceGeneration, profile, sourceConfiguration, parameters, caller, correlationId, acquisitionId]
              additionalProperties: false
            steps:
              - { type: input, name: input, displayName: Acquisition request }
              - type: toolRoute
                name: crawl
                displayName: Crawl configured Web origin
                toolSet: { resourceId: crawl4ai-web-acquisition, version: 1.0.0 }
                capability: web.crawl
                route: default
                argumentsMapping: { startUrl: "${input.sourceConfiguration.url}", maximumDepth: "${input.sourceConfiguration.maximumDepth}", maximumPages: "${input.sourceConfiguration.maximumPages}", correlationId: "${input.correlationId}" }
              - type: toolRoute
                name: create
                displayName: Create staged corpus
                toolSet: { resourceId: staged-artifacts-builtin, version: 1.0.0 }
                capability: staged-artifact.create
                route: default
                argumentsMapping: { fileName: crawl4ai-content.md, mediaType: "${steps.crawl.output.structuredContent.corpus.mediaType}" }
              - type: repeat
                name: transfer
                displayName: Transfer bounded content chunks
                flow: { resourceId: crawl4ai-web-transfer-chunk, versionStrategy: exact, version: 1.0.0 }
                inputMapping: { contentReference: "${steps.crawl.output.structuredContent.corpus.reference}", stagedArtifactId: "${steps.create.output.artifactReference}", offset: 0 }
                nextInputMapping: { contentReference: "${steps.transfer.output.contentReference}", stagedArtifactId: "${steps.transfer.output.stagedArtifactId}", offset: "${steps.transfer.output.nextOffset}" }
                until: "${steps.transfer.output.endOfContent == true}"
                maximumIterations: 1000
              - type: toolRoute
                name: seal
                displayName: Seal staged corpus
                toolSet: { resourceId: staged-artifacts-builtin, version: 1.0.0 }
                capability: staged-artifact.seal
                route: default
                argumentsMapping: { artifactId: "${steps.create.output.artifactReference}" }
              - type: flow
                name: persist
                displayName: Persist acquired corpus
                flow: { resourceId: artifact-storage-filesystem-write-builtin, versionStrategy: active }
                inputMapping: { stagedArtifactId: "${steps.create.output.artifactReference}", producerFlowRunId: "${execution.flowRunId}", producerFlowStepId: seal }
              - type: toolRoute
                name: cleanup
                displayName: Delete temporary Crawl4AI content
                toolSet: { resourceId: crawl4ai-web-acquisition, version: 1.0.0 }
                capability: content.delete
                route: default
                argumentsMapping: { contentReference: "${steps.crawl.output.structuredContent.corpus.reference}" }
              - type: output
                name: output
                displayName: Publishable acquired Artifact
                outputMapping:
                  artifacts:
                    - { artifactId: "${steps.persist.output.flowRunArtifactId}", kind: durable, disposition: publishable, name: "${input.dataSourceId}", mediaType: "${steps.seal.output.MediaType}", digest: "${steps.seal.output.Sha256}" }
            transitions:
              - { id: input-crawl, fromStep: input, event: completed, toStep: crawl }
              - { id: crawl-create, fromStep: crawl, event: completed, toStep: create }
              - { id: create-transfer, fromStep: create, event: completed, toStep: transfer }
              - { id: transfer-seal, fromStep: transfer, event: completed, toStep: seal }
              - { id: seal-persist, fromStep: seal, event: completed, toStep: persist }
              - { id: persist-cleanup, fromStep: persist, event: completed, toStep: cleanup }
              - { id: cleanup-output, fromStep: cleanup, event: completed, toStep: output }
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
        metadata: { name: crawl4ai-web }
        definition:
          displayName: Crawl4AI Web
          description: Acquires any allowed Web origin through Crawl4AI as one governed corpus Artifact.
          enabled: true
          version: 1.0.0
          configurationSchema:
            type: object
            properties:
              url: { type: string, format: uri, minLength: 8, maxLength: 2048 }
              maximumDepth: { type: integer, minimum: 0, maximum: 10 }
              maximumPages: { type: integer, minimum: 1, maximum: 1000 }
            required: [url, maximumDepth, maximumPages]
            additionalProperties: false
          acquisitionFlow: { name: crawl4ai-web-acquisition, version: 1.0.0, useActiveVersion: false }
          toolBindings:
            - { name: crawl, capability: web.crawl, tool: { binding: web.crawl } }
            - { name: read, capability: content.read, tool: { binding: content.read } }
            - { name: delete, capability: content.delete, tool: { binding: content.delete } }
          limits: { maximumPages: 1000, maximumDepth: 10 }
          policies: { publicNetworkOnly: true }
        """;
}
