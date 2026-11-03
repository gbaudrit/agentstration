import { expect, type APIResponse, type Page } from '@playwright/test';

type ResourceId = string | { value: string };
interface StagedArtifactWire { artifactId: ResourceId; }
interface FlowRunWire { id: string; status: number; output?: { flowRunArtifactId?: string }; error?: { message?: string }; }

export class SourceLifecycleFixtures {
  public constructor(private readonly page: Page) {}
  public async grantBuiltinAccess(consoleUrl: string, tenantScope: string, additionalNames: readonly string[] = []): Promise<void> {
    const headers = { 'X-Agentstration-Context': '/instance' };
    const response = await this.page.request.get(`${consoleUrl}/api/resource-scopes`, { headers });
    await expectApi(response, 200, 'read built-in resource identities');
    const inventory = await response.json() as { scopes: { scopeRef: string; resources: { uid: string; name: string; kind: string; namespace: string; generation?: number }[] }[] };
    const names = new Set(['knowledge-projection-builtin', 'knowledge-retrieval-builtin',
      'data-source-artifact-import-acquisition-builtin',
      'artifact.import', 'knowledge.retrieval.search', 'knowledge.retrieval.query', 'knowledge.retrieval.read', ...additionalNames]);
    const resources = inventory.scopes.find(scope => scope.scopeRef === '/instance')!.resources.filter(resource => names.has(resource.name));
    expect(resources.some(resource => resource.kind === 'Flow' && resource.name === 'knowledge-retrieval-builtin')).toBe(true);
    for (const resource of resources) {
      const url = `${consoleUrl}/api/resource-access-policies/${resource.uid}`;
      const current = await this.page.request.get(url, { headers });
      expect([200, 404]).toContain(current.status());
      const grants = current.status() === 200 ? ((await current.json()) as { grants: { targetScopeRef: string; actions: string[]; includeDescendants: boolean }[] }).grants : [];
      const saved = await this.page.request.put(url, {
        headers: { ...headers, ...(current.status() === 200 ? { 'If-Match': current.headers()['etag']! } : {}) },
        data: {
          resource: { uid: resource.uid, ownerScopeRef: '/instance', namespace: resource.namespace, kind: resource.kind, generation: resource.generation },
          grants: [...grants.filter(grant => grant.targetScopeRef !== tenantScope),
            { targetScopeRef: tenantScope, actions: ['discover', 'use'], includeDescendants: true }],
        },
      });
      await expectApi(saved, 200, `grant built-in '${resource.name}' to the fixture Tenant`);
    }
  }

  public async createPublishedFlow(consoleUrl: string, name: string, displayName: string, contract: string,
    source: string, scopeRef?: string): Promise<void> {
    const headers: Record<string, string> = scopeRef ? { 'X-Agentstration-Context': scopeRef } : {};
    const query = scopeRef ? `?scopeRef=${encodeURIComponent(scopeRef)}` : '';
    const created = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      headers, data: { name, displayName, scopeRef, template: 'Empty', tags: { 'flow.contract': contract } },
    });
    await expectApi(created, 201, `create Flow '${name}'`);
    const etag = created.headers()['etag'];
    if (!etag) throw new Error(`Flow '${name}' draft did not expose an ETag.`);
    await expectApi(await this.page.request.put(`${consoleUrl}/api/flows/${name}/draft/source${query}`, {
      headers: { ...headers, 'If-Match': etag }, data: { source, format: 'yaml', updatedBy: 'playwright' },
    }), 200, `replace Flow '${name}' source`);
    const validation = await this.page.request.post(`${consoleUrl}/api/flows/${name}/validate${query}`, { headers });
    await expectApi(validation, 200, `validate Flow '${name}'`);
    const validationBody = await validation.json() as { isValid: boolean; issues?: unknown[] };
    expect(validationBody.isValid, JSON.stringify(validationBody.issues)).toBe(true);
    await expectApi(await this.page.request.post(`${consoleUrl}/api/flows/${name}/publish${query}`, {
      headers, data: { version: '1.0.0', releaseNotes: 'Schema-driven source browser fixture', activate: true },
    }), 201, `publish Flow '${name}'`);
  }

  public async createDurableArtifact(consoleUrl: string, suffix: string): Promise<string> {
    const created = await this.page.request.post(`${consoleUrl}/api/artifacts/staged`, {
      data: {
        fileName: `schema-input-${suffix}.txt`, mediaType: 'text/plain',
        producer: { kind: 0, id: `schema-input-${suffix}`, flowRunId: `schema-input-${suffix}`, flowStepId: 'fixture' },
      },
    });
    await expectApi(created, 201, 'create staged Artifact');
    const staged = await created.json() as StagedArtifactWire;
    const stagedId = identifier(staged.artifactId);
    await expectApi(await this.page.request.post(`${consoleUrl}/api/artifacts/staged/${stagedId}/content`, {
      data: { offset: 0, contentBase64: Buffer.from(`Schema-driven source ${suffix}`).toString('base64') },
    }), 200, 'write staged Artifact');
    await expectApi(await this.page.request.post(`${consoleUrl}/api/artifacts/staged/${stagedId}/seal`), 200, 'seal staged Artifact');
    const runResponse = await this.page.request.post(`${consoleUrl}/api/namespaces/default/flows/artifact-storage-filesystem-write-builtin/runs`, {
      data: { input: { stagedArtifactId: stagedId, producerFlowRunId: `schema-input-${suffix}`, producerFlowStepId: 'fixture' }, version: '1.0.0' },
    });
    await expectApi(runResponse, 202, 'persist staged Artifact');
    let run = await runResponse.json() as FlowRunWire;
    await expect.poll(async () => {
      run = await this.read<FlowRunWire>(`${consoleUrl}/api/flowRuns/${encodeURIComponent(run.id)}`);
      return run.status;
    }, { timeout: 90_000 }).toBe(3);
    if (!run.output?.flowRunArtifactId) throw new Error(`Artifact storage Flow failed: ${run.error?.message ?? 'missing output'}.`);
    return run.output.flowRunArtifactId;
  }

  private async read<T>(url: string): Promise<T> {
    const response = await this.page.request.get(url);
    await expectApi(response, 200, `read '${url}'`);
    return await response.json() as T;
  }
}

async function expectApi(response: APIResponse, status: number, operation: string): Promise<void> {
  if (response.status() === status) return;
  throw new Error(`Unable to ${operation}: HTTP ${response.status()} ${await response.text()}`);
}

function identifier(value: ResourceId): string {
  return (typeof value === 'string' ? value : value.value).replaceAll('-', '');
}

export function configurationSchema(): Record<string, unknown> {
  return {
    type: 'object',
    properties: {
      endpoint: { type: 'string', title: 'Endpoint', description: 'Source endpoint.', format: 'uri', minLength: 8 },
      mode: { type: 'string', title: 'Mode', enum: ['compact', 'full'], default: 'compact' },
      options: {
        type: 'object', title: 'Options', properties: {
          retries: { type: 'integer', title: 'Retries', minimum: 1, maximum: 5, default: 2 },
        }, additionalProperties: false,
      },
    },
    required: ['endpoint', 'mode', 'options'], additionalProperties: false,
  };
}

export function acquisitionFlowSource(artifactId: string): string {
  return `entryStep: input
inputSchema:
  type: object
  properties:
    dataSourceId: { type: string }
    dataSourceUid: { type: string }
    dataSourceGeneration: { type: integer }
    profile: { type: object }
    sourceConfiguration: { type: object }
    parameters:
      type: object
      properties:
        query: { type: string, title: Acquisition query, minLength: 3 }
        limit: { type: integer, title: Result limit, minimum: 1, maximum: 5, default: 2 }
      required: [query, limit]
      additionalProperties: false
    caller: { type: object }
    correlationId: { type: string }
    acquisitionId: { type: string }
  required: [dataSourceId, dataSourceUid, dataSourceGeneration, profile, sourceConfiguration, parameters, caller, correlationId, acquisitionId]
  additionalProperties: false
outputSchema:
  type: object
  properties:
    artifacts: { type: array }
  required: [artifacts]
  additionalProperties: false
steps:
- type: input
  name: input
  schema:
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
- type: output
  name: output
  outputMapping:
    artifacts:
    - artifactId: ${artifactId}
      kind: durable
      disposition: publishable
      name: schema-input.txt
      mediaType: text/plain
transitions:
- { id: input-output, fromStep: input, event: completed, toStep: output }
`;
}

export function transformationFlowSource(): string {
  return passthroughFlowSource([
    'bindingName', 'dataSource', 'artifacts', 'configuration', 'caller', 'correlationId', 'transformationId',
  ], `configuration:
  type: object
  properties:
    prefix: { type: string, title: Prefix, minLength: 3 }
    mode: { type: string, title: Transformation mode, enum: [compact, full], default: compact }
  required: [prefix, mode]
  additionalProperties: false`);
}

export function projectionFlowSource(): string {
  return passthroughFlowSource([
    'knowledgeSourceId', 'knowledgeSourceUid', 'knowledgeSourceGeneration', 'inputs', 'artifacts', 'parameters',
    'caller', 'correlationId', 'projectionId',
  ], `parameters:
  type: object
  properties:
    audience: { type: string, title: Audience, minLength: 3 }
    includeMetadata: { type: boolean, title: Include metadata, default: true }
  required: [audience, includeMetadata]
  additionalProperties: false`);
}

export function passthroughFlowSource(required: readonly string[], specializedProperty: string): string {
  const properties = required.map(name => specializedProperty.startsWith(`${name}:`)
    ? specializedProperty
    : `${name}: { type: ${name === 'artifacts' || name === 'inputs' ? 'array' : name.endsWith('Generation') ? 'integer' : name === 'configuration' || name === 'parameters' || name === 'caller' || name === 'dataSource' ? 'object' : 'string'} }`).join('\n');
  const inputProperties = indent(properties, 4);
  const stepProperties = indent(properties, 6);
  return `entryStep: input
inputSchema:
  type: object
  properties:
${inputProperties}
  required: [${required.join(', ')}]
  additionalProperties: false
outputSchema:
  type: object
  properties:
    artifacts: { type: array }
  required: [artifacts]
  additionalProperties: false
steps:
- type: input
  name: input
  schema:
    type: object
    properties:
${stepProperties}
    required: [${required.join(', ')}]
- type: output
  name: output
  outputMapping:
    artifacts: \${input.artifacts}
transitions:
- { id: input-output, fromStep: input, event: completed, toStep: output }
`;
}

function indent(value: string, spaces: number): string {
  const prefix = ' '.repeat(spaces);
  return value.split('\n').map(line => `${prefix}${line}`).join('\n');
}
