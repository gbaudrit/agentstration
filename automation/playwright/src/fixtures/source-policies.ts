import { createHash } from 'node:crypto';
import { expect, type Page } from '@playwright/test';
import { SourceLifecycleFixtures } from './source-lifecycle.js';

export interface SourcePolicyFixture {
  suffix: string;
  operation: any;
  service: any;
  storage: any;
  storageRef: any;
  operationRef: any;
}

/** Synthetic, deterministic read-only native operation. No external content or provider. */
export class SourcePolicyFixtures {
  public constructor(private readonly page: Page, private readonly base: string) {}
  public async prepare(): Promise<SourcePolicyFixture> {
    const page = this.page; const base = this.base;
    const headers = { 'X-Agentstration-Context': '/instance' };
    const api = async (method: string, path: string, data?: unknown, extra?: Record<string, string>) => {
      const response = await page.request.fetch(base + path, { method, headers: { ...headers, ...extra }, data });
      expect(response.ok(), `${method} ${path}: ${await response.text()}`).toBe(true);
      return { value: await response.json(), etag: response.headers()['etag'] };
    };
    const suffix = `policy-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 6)}`;
    const fixtures = new SourceLifecycleFixtures(page);
    async function flow(name: string, contract: string, output: unknown) {
      const source = `name: ${name}\nversion: 1.0.0\nmetadata:\n  flow.contract: ${contract}\nentryStep: input\ninputSchema: {type: object}\noutputSchema: {type: object}\nsteps:\n- {type: input, name: input, schema: {type: object}}\n- type: output\n  name: output\n  outputMapping: ${JSON.stringify(output)}\ntransitions:\n- {id: input-output, fromStep: input, event: completed, toStep: output}\n`;
      await fixtures.createPublishedFlow(base, name, name, contract, source, '/instance');
      const inventory = (await api('GET', '/api/resource-scopes')).value;
      const resource = inventory.scopes.flatMap((scope: any) => scope.resources).find((value: any) => value.name === name && value.kind === 'Flow');
      const version = (await api('GET', `/api/flows/${name}/versions/1.0.0?scopeRef=%2Finstance`)).value;
      return { flow: { name, scopeRef: '/instance' }, flowUid: resource.uid, version: '1.0.0', definitionHash: version.definitionHash, contract };
    }
    const schema = { type: 'object', additionalProperties: true };
    const schemaDigest = 'sha256:' + createHash('sha256').update(JSON.stringify(schema)).digest('hex');
    const acquireFlow = await flow(`acquire-${suffix}`, 'connected-service-operation-acquisition/v1', { dataset: '${input.parameters.dataset}' });
    const type = (await api('POST', '/api/connected-service-operation-types', {
      name: `op-${suffix}`, scopeRef: '/instance', properties: { displayName: `Operation ${suffix}`, preferredVersion: '1.0.0', versions: [{
        version: '1.0.0', schemaDigest, definitionSchema: schema, interaction: 'request-response',
        invocation: { providerOperationId: 'fixture.read', contractVersion: '1.0.0' }, acquisition: { flow: acquireFlow },
      }] },
    })).value;
    const typeRef = { type: { name: type.metadata.name, scopeRef: '/instance' }, typeUid: type.uid, version: '1.0.0', schemaDigest };
    const inputSchema = { type: 'object', properties: { dataset: { type: 'string' } }, required: ['dataset'], additionalProperties: false };
    const discoveryFlow = await flow(`discover-${suffix}`, 'connected-service-operation-discovery/v1', { operations: [{
      externalId: 'fixture.read', displayName: 'Read browser fixture', type: typeRef,
      definition: { readOnly: true, inputSchema, outputSchema: { type: 'object' } },
    }] });
    const serviceType = (await api('POST', '/api/connected-service-types', { name: `service-type-${suffix}`, scopeRef: '/instance', properties: {
      displayName: 'Browser service type', preferredVersion: '1.0.0', versions: [{ version: '1.0.0', schemaDigest, schema, inputs: [], exports: [],
        operationDiscovery: { flow: discoveryFlow, producedTypes: [typeRef] } }],
    } })).value;
    const service = (await api('POST', '/api/connected-services', { name: `service-${suffix}`, scopeRef: '/instance', properties: {
      displayName: 'Browser service', type: { type: { name: serviceType.metadata.name, scopeRef: '/instance' }, typeUid: serviceType.uid, version: '1.0.0', schemaDigest }, options: {},
    } })).value;
    const serviceName = service.metadata.name;
    const discovery = (await api('POST', `/api/connected-services/${serviceName}/operation-discoveries?scopeRef=%2Finstance`, {})).value;
    let discovered: any;
    await expect.poll(async () => {
      discovered = (await api('GET', `/api/connected-service-operation-discoveries/${discovery.metadata.name}`)).value;
      return Boolean(discovered.completedAt);
    }, { timeout: 90_000 }).toBe(true);
    expect(discovered.errorCode, JSON.stringify(discovered)).toBeFalsy();
    const operations = (await api('GET', `/api/connected-services/${serviceName}/operations?scopeRef=%2Finstance`)).value;
    let operation = (Array.isArray(operations) ? operations : operations.value)[0];
    const operationName = operation.metadata.name;
    const operationRecord = await api('GET', `/api/connected-service-operations/${operationName}?scopeRef=%2Finstance`);
    operation = (await api('PUT', `/api/connected-service-operations/${operationName}/enabled?scopeRef=%2Finstance`, { enabled: true }, { 'If-Match': operationRecord.etag! })).value;
    const storages = (await api('GET', '/api/storages?scopeRef=%2Finstance')).value.value;
    const storage = storages.find((value: any) => value.metadata.name === 'local-default');
    const storageRef = { storage: { scopeRef: '/instance', namespace: 'default', kind: 'Storage', name: 'local-default' }, storageUid: storage.uid,
      generation: storage.generation, bindingDigest: storage.observedState.bindingDigest };

    const operationRef = { operation: { scopeRef: '/instance', namespace: 'default', kind: 'ConnectedServiceOperation', name: operationName },
      operationUid: operation.uid, generation: operation.generation, definitionDigest: operation.definition.definitionDigest };
    return { suffix, operation, service, storage, storageRef, operationRef };
  }
  public async api(method: string, path: string, data?: unknown, extra?: Record<string, string>): Promise<{ value: any; etag?: string }> {
    const response = await this.page.request.fetch(this.base + path, { method, headers: { 'X-Agentstration-Context': '/instance', ...extra }, data });
    expect(response.ok(), `${method} ${path}: ${await response.text()}`).toBe(true);
    return { value: await response.json(), etag: response.headers()['etag'] };
  }
  public async source(fixture: SourcePolicyFixture, name: string, lifecycle: unknown, direct = false, dataset = `synthetic-${name}`): Promise<void> {
    await this.api('POST', '/api/datasources', { name, scopeRef: '/instance', properties: { displayName: name, dataset: {
      storage: direct ? null : fixture.storageRef, lifecycle, rule: { write: true },
      acquisition: { operation: fixture.operationRef, parameters: { dataset } },
    } } });
  }
  public async knowledge(fixture: SourcePolicyFixture, name: string, inputs: string[], orchestrate = false): Promise<void> {
    await this.api('POST', '/api/knowledgesources', { name, scopeRef: '/instance', properties: { displayName: name,
      enabled: true, dataSources: inputs.map((source, index) => ({ name: `input-${index + 1}`, dataSource: { name: source, scopeRef: '/instance' } })),
      ...(orchestrate ? { orchestration: { resourceId: 'knowledge-orchestration-builtin', scopeRef: '/instance', namespace: 'default', versionStrategy: 'exact', version: '1.0.0' } } : {}),
      projectionFlow: { name: 'knowledge-projection-builtin', scopeRef: '/instance', version: '1.0.0', useActiveVersion: false },
      output: { storage: fixture.storageRef, rule: { write: true } },
    } });
  }
}
