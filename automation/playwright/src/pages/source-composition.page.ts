import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { SourceLifecycleFixtures } from '../fixtures/source-lifecycle.js';
import { SourcePolicyFixtures } from '../fixtures/source-policies.js';
import { SourcePoliciesPage } from './source-policies.page.js';

export class SourceCompositionPage {
  public constructor(private readonly page: Page) {}
  public get evidence(): Locator { return this.page.getByTestId(TestIds.sourceLifecycle.projectionProvenance); }

  public async exercise(base: string): Promise<void> {
    this.page.setDefaultTimeout(15_000);
    const api = new SourcePolicyFixtures(this.page, base);
    const fixture = await api.prepare();
    const flows = new SourceLifecycleFixtures(this.page);
    const nativeChild = `native-transform-${fixture.suffix}`;
    const incompatible = `incompatible-${fixture.suffix}`;
    const custom = `custom-root-${fixture.suffix}`;
    const object = { type: 'object' };
    const parameters = { type: 'object', properties: { tag: { type: 'string' } }, required: ['tag'] };
    const childInput = { type: 'object', properties: { value: object, parameters }, required: ['value', 'parameters'] };
    const publish = async (name: string, contract: string, input: unknown, output: unknown, steps: unknown[], transitions?: unknown[], scopeRef = '/instance') => {
      const source = JSON.stringify({ name, version: '1.0.0', metadata: { 'flow.contract': contract }, entryStep: 'input',
        inputSchema: input, outputSchema: output, steps: [{ type: 'input', name: 'input', schema: input }, ...steps],
        transitions: transitions ?? [{ id: 'next', fromStep: 'input', event: 'completed', toStep: 'output' }],
      });
      await flows.createPublishedFlow(base, name, name, contract, source, scopeRef);
    };
    await publish(nativeChild, 'fixture.transform/v1', childInput, object, [{ type: 'output', name: 'output', schema: object,
      outputMapping: { dataset: '${input.value.dataset}', stage: '${input.parameters.tag}', previous: '${input.value}' } }]);
    await publish(incompatible, 'fixture.transform/v1', { ...childInput, properties: { value: { type: 'array' }, parameters } }, object,
      [{ type: 'output', name: 'output', schema: object, outputMapping: {} }]);
    await publish(custom, 'datasource.orchestration/v1', object, object, [
      { type: 'composition', name: 'acquisition', point: 'acquisition', allowEmpty: false, inputSchema: object, outputSchema: object, inputMapping: '${input}' },
      { type: 'transform', name: 'anonymize', mapping: { dataset: 'redacted' }, schema: object },
      { type: 'composition', name: 'extensions', displayName: 'Extensions', point: 'prepare', allowEmpty: true, inputSchema: object, outputSchema: object, inputMapping: '${steps.anonymize.output}' },
      { type: 'output', name: 'output', schema: object, outputMapping: '${steps.extensions.output}' },
    ], ['input', 'acquisition', 'anonymize', 'extensions'].map((step, index) => ({ id: `next-${index}`, fromStep: step,
      event: 'completed', toStep: ['acquisition', 'anonymize', 'extensions', 'output'][index] })));
    await this.authorPoint(base, custom);
    const source = `composed-${fixture.suffix}`;
    await api.source(fixture, source, {});
    const editor = new SourcePoliciesPage(this.page);
    await editor.openDataset(base, source);
    await this.page.getByTestId(TestIds.sourcePolicies.root).selectOption(`/instance|default|${custom}|2.0.0`);
    await editor.saveDataset(base, source, api);
    const empty = await this.acquire(base, source, api);
    expect(empty.content.dataset).toBe('redacted');
    await editor.openDataset(base, source);
    const sequence = this.page.getByTestId(TestIds.sourcePolicies.sequence).filter({ has: this.page.getByRole('heading', { name: 'redactedTransformations', exact: true }) });
    await this.append(sequence, nativeChild, 'first');
    await this.append(sequence, nativeChild, 'second');
    await sequence.getByTestId(TestIds.sourcePolicies.call).nth(1).getByTestId(TestIds.sourcePolicies.up).click();
    await expect(sequence.getByTestId(TestIds.sourcePolicies.call).nth(0).locator('[data-schema-path="$.tag"] input')).toHaveValue('second');
    await editor.saveDataset(base, source, api);
    await editor.openDataset(base, source);
    await expect(sequence.getByTestId(TestIds.sourcePolicies.call).nth(0).locator('[data-schema-path="$.tag"] input')).toHaveValue('second');
    await expect(sequence.getByTestId(TestIds.sourcePolicies.call).nth(1).locator('[data-schema-path="$.tag"] input')).toHaveValue('first');
    const transformed = await this.acquire(base, source, api);
    expect(transformed.content.stage).toBe('first');
    expect(transformed.content.previous.stage).toBe('second');
    expect(transformed.content.previous.previous.dataset).toBe('redacted');
    const identity = (await api.api('GET', '/api/identity/context')).value;
    const forbidden = `descendant-only-${fixture.suffix}`;
    expect(identity.availableWorkspaces.length).toBeGreaterThan(0);
    const forbiddenScope = `/workspaces/${identity.availableWorkspaces[0].id}`;
    await publish(forbidden, 'fixture.transform/v1', childInput, object,
      [{ type: 'output', name: 'output', schema: object, outputMapping: '${input.value}' }], undefined, forbiddenScope);
    const current = await api.api('GET', `/api/datasources/${source}?scopeRef=%2Finstance`);
    const forbiddenProperties = structuredClone(current.value.definition);
    forbiddenProperties.dataset.compositions.redactedTransformations = [{ flow: {
      resourceId: forbidden, scopeRef: forbiddenScope, namespace: 'default', versionStrategy: 'exact', version: '1.0.0',
    }, parameters: { tag: 'forbidden' } }];
    const refused = await this.page.request.put(`${base}/api/datasources/${source}?scopeRef=%2Finstance`, {
      headers: { 'X-Agentstration-Context': '/instance', 'If-Match': current.etag! }, data: { properties: forbiddenProperties },
    });
    expect([400, 403, 422]).toContain(refused.status());
    expect((await refused.json()).type).toContain('data_source_composition_invalid');
    await editor.openDataset(base, source);
    await expect(sequence.getByTestId(TestIds.sourcePolicies.call).nth(0).getByTestId(TestIds.sourcePolicies.target)).not.toContainText(forbidden);
    await sequence.getByTestId(TestIds.sourcePolicies.call).nth(0).getByTestId(TestIds.sourcePolicies.target).selectOption(`/instance|default|${incompatible}|1.0.0`);
    await sequence.getByTestId(TestIds.sourcePolicies.call).nth(0).locator('[data-schema-path="$.tag"] input').fill('invalid');
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave).click();
    await expect(this.page.getByTestId(TestIds.sourcePolicies.datasetError)).toContainText('data_source_composition_invalid');
    await expect(sequence.getByTestId(TestIds.sourcePolicies.call).nth(1).locator('[data-schema-path="$.tag"] input')).toHaveValue('first');

    const second = `second-${fixture.suffix}`; const knowledge = `composed-knowledge-${fixture.suffix}`;
    await api.source(fixture, second, {});
    const schema = (await api.api('GET', '/api/flows/knowledge-projection-builtin/versions/1.0.0?scopeRef=%2Finstance')).value.graph.outputSchema;
    const manifestChild = `manifest-${fixture.suffix}`; const failChild = `reject-${fixture.suffix}`;
    const manifestInput = { type: 'object', properties: { value: schema, parameters }, required: ['value', 'parameters'] };
    await publish(manifestChild, 'fixture.manifest/v1', manifestInput, schema,
      [{ type: 'output', name: 'output', schema, outputMapping: '${input.value}' }]);
    await publish(failChild, 'fixture.manifest/v1', manifestInput, schema, [
      { type: 'condition', name: 'decide', left: '${input.parameters.tag}', operator: 'equals', right: 'allow' },
      { type: 'output', name: 'approved', outcome: 'success', schema, outputMapping: '${input.value}' },
      { type: 'output', name: 'rejected', outcome: 'error', code: 'fixture_post_aggregate_failed', message: 'Synthetic post-aggregation failure.' },
    ], [{ id: 'decide', fromStep: 'input', event: 'completed', toStep: 'decide' },
      { id: 'approve', fromStep: 'decide', event: 'true', toStep: 'approved' },
      { id: 'reject', fromStep: 'decide', event: 'false', toStep: 'rejected' }]);
    await api.knowledge(fixture, knowledge, [source, second]);
    await editor.openKnowledge(base, knowledge);
    const bindings = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBinding);
    await this.append(bindings.nth(0).getByTestId(TestIds.sourcePolicies.sequence), manifestChild, 'input-one');
    await this.append(bindings.nth(1).getByTestId(TestIds.sourcePolicies.sequence), manifestChild, 'input-two');
    const global = this.page.getByTestId(TestIds.sourcePolicies.composition).getByTestId(TestIds.sourcePolicies.sequence);
    await this.append(global, manifestChild, 'aggregate-once');
    await editor.saveKnowledge(knowledge, api);
    const projection = await editor.project(base, knowledge, api);
    expect(projection.inputs.map((value: any) => value.bindingName)).toEqual(['input-1', 'input-2']);
    expect(projection.inputs[0].transformationComposition.receipt.bindings.transformations[0].parameters.tag).toBe('input-one');
    expect(projection.inputs[1].transformationComposition.receipt.bindings.transformations[0].parameters.tag).toBe('input-two');
    expect(projection.projectionComposition.receipt.bindings.postAggregate).toHaveLength(1);
    await this.page.getByTitle(projection.metadata.name, { exact: true }).click();
    await expect(this.evidence).toContainText('input-1');
    await expect(this.evidence).toContainText('input-2');
    await editor.openKnowledge(base, knowledge);
    await global.getByTestId(TestIds.sourcePolicies.target).selectOption(`/instance|default|${failChild}|1.0.0`);
    await global.locator('[data-schema-path="$.tag"] input').fill('reject-publication');
    await editor.saveKnowledge(knowledge, api);
    await editor.project(base, knowledge, api, true);
    expect((await api.api('POST', `/api/knowledgesources/${knowledge}/content/status?scopeRef=%2Finstance`, {})).value.activeSnapshotId).toBe(projection.snapshotName);
    await this.page.getByTitle(projection.metadata.name, { exact: true }).click();
    await expect(this.evidence).toBeVisible();
    await editor.assertResponsive();
  }

  private async append(sequence: Locator, name: string, tag: string): Promise<void> {
    const rows = sequence.getByTestId(TestIds.sourcePolicies.call);
    const count = await rows.count();
    await sequence.getByTestId(TestIds.sourcePolicies.add).click();
    await expect(rows).toHaveCount(count + 1);
    const row = rows.nth(count);
    const target = row.getByTestId(TestIds.sourcePolicies.target);
    await target.selectOption(`/instance|default|${name}|1.0.0`);
    await expect(row).toHaveAttribute('data-flow-reference', `/instance|default|${name}|1.0.0`);
    await row.locator('[data-schema-path="$.tag"] input').fill(tag);
    await row.locator('[data-schema-path="$.tag"] input').blur();
    await expect(row.getByRole('alert')).toHaveCount(0);
  }
  private async acquire(base: string, name: string, api: SourcePolicyFixtures): Promise<{ acquisition: any; content: any }> {
    const before = (await api.api('GET', `/api/datasources/${name}/acquisitions?scopeRef=%2Finstance`)).value;
    const prior = new Set((Array.isArray(before) ? before : before.value).map((value: any) => value.metadata.name));
    await this.page.goto(`${base}/data-sources/${name}?context=%2Finstance&scopeRef=%2Finstance`);
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceStartAcquisition).click();
    let acquisition: any;
    await expect.poll(async () => {
      const values = (await api.api('GET', `/api/datasources/${name}/acquisitions?scopeRef=%2Finstance`)).value;
      const candidates = Array.isArray(values) ? values : values.value;
      for (const value of candidates.filter((value: any) => !prior.has(value.metadata.name))) {
        const current = (await api.api('GET', `/api/datasourceacquisitions/${value.metadata.name}`)).value;
        if (current.completedAt) acquisition = current;
      }
      return Boolean(acquisition);
    }, { timeout: 90_000 }).toBe(true);
    expect(acquisition.errorCode, JSON.stringify(acquisition)).toBeFalsy();
    const content = (await api.api('GET', `/api/datasourceacquisitions/${acquisition.metadata.name}/content`)).value;
    return { acquisition, content: JSON.parse(Buffer.from(content.contentBase64, 'base64').toString('utf8')) };
  }
  private async authorPoint(base: string, name: string): Promise<void> {
    await this.page.goto(`${base}/flows/${name}/designer?scopeRef=%2Finstance&context=%2Finstance`);
    await expect(this.page.getByTestId(TestIds.flowObservability.designer)).toBeVisible();
    const settings = this.page.locator('.flow-node').filter({ has: this.page.locator('strong', { hasText: /^Extensions$/ }) }).locator('.flow-node-settings');
    await expect(async () => {
      if (!await this.page.getByTestId(TestIds.flowObservability.stepSettingsConfigurationTab).isVisible()) {
        const button = await settings.boundingBox();
        const zoom = await this.page.locator('.flow-zoom-controls').boundingBox();
        if (button && zoom && button.y < zoom.y + zoom.height && button.y + button.height > zoom.y
          && button.x < zoom.x + zoom.width && button.x + button.width > zoom.x) {
          const canvas = await this.page.locator('.flow-diagram-canvas').boundingBox();
          if (!canvas) throw new Error('The composition diagram is not measurable.');
          const x = canvas.x + canvas.width * 0.1; const y = canvas.y + canvas.height * 0.45;
          await this.page.mouse.move(x, y); await this.page.mouse.down();
          await this.page.mouse.move(x, y + 150, { steps: 10 }); await this.page.mouse.up();
        }
        await settings.click({ timeout: 2_000 });
      }
      await expect(this.page.getByTestId(TestIds.flowObservability.stepSettingsConfigurationTab)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    await this.page.getByTestId(TestIds.flowObservability.stepSettingsConfigurationTab).click();
    await this.page.getByTestId(TestIds.sourcePolicies.pointName).fill('redactedTransformations');
    await this.page.getByTestId(TestIds.sourcePolicies.pointName).blur();
    await this.page.locator('.step-settings-close').click();
    await this.page.getByTestId(TestIds.flowObservability.designerSave).click();
    await expect(this.page.locator('.save-state.saved')).toBeVisible();
    if (!await this.page.locator('.publish-version input').isVisible()) await this.page.locator('.designer-more-toggle').click();
    await this.page.locator('.publish-version input').fill('2.0.0');
    await this.page.locator('.publish-version input').blur();
    await this.page.getByTestId(TestIds.flowObservability.designerPublish).click();
    await expect(this.page.getByTestId(TestIds.flowObservability.designerPublicationSuccess)).toBeVisible();
  }
}
