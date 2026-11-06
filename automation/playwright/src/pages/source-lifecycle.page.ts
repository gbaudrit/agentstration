import { expect, type APIResponse, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { SourceLifecycleFixtures, acquisitionFlowSource, projectionFlowSource } from '../fixtures/source-lifecycle.js';
import type { ExecutionScopeFixture } from './execution-scopes.page.js';
import type { ExpectedText } from '../locales/expected-text.js';
import { LoginPage } from './login.page.js';

interface ResourceWire {
  uid: string;
  generation: number;
  metadata: { name: string };
  definition: Record<string, unknown>;
  errorMessage?: string | null;
}
interface Acquisition extends ResourceWire {
  state: number;
  flowRunId: string;
  composition: { profile: { name: string; version: string; definitionHash: string; dependencyHash: string }; flow: { name: string; version: string }; tools: unknown[] };
  sourceConfiguration: Record<string, unknown>;
  manifest: { artifacts: { artifactId: string }[] };
}
interface Projection extends ResourceWire {
  state: number;
  snapshotName: string | null;
  projectionFlow: { name: string; version: string };
  retrievalFlow: { name: string; version: string };
  inputs: { bindingName: string; acquisitionId: string; acquisitionComposition: Acquisition['composition']; acquiredArtifacts: { artifactId: string }[]; preparedArtifacts: { artifactId: string }[] }[];
  inputIssues: { bindingName: string; code: string }[];
}
interface Snapshot extends ResourceWire {
  projectionId: string;
  projectionInputs: Projection['inputs'];
  projectionFlow: Projection['projectionFlow'];
  retrievalFlow: Projection['retrievalFlow'];
  artifacts: { artifactId: string; storageFlowRunId: string; sha256: string; length: number }[];
}
interface LifecycleFixture {
  scope: string;
  profile: string;
  acquisitionFlow: string;
  projectionFlow: string;
  artifactId: string;
  artifactIds: string[];
  sources: string[];
  knowledge: string;
}

/** Reusable journeys for #731 and #732. API calls provision prerequisites and verify
 * immutable evidence; lifecycle actions and expected failure feedback use the Console. */
export class SourceLifecyclePage {
  private fixture!: LifecycleFixture;
  private scope!: string;
  public constructor(private readonly page: Page) {}
  public get projectionFixture(): Readonly<LifecycleFixture> { return this.fixture; }
  public get acquisitionHistory(): Locator { return this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitions); }
  public get snapshots(): Locator { return this.page.getByTestId(TestIds.sourceLifecycle.snapshots); }

  public get failureTopology(): Locator { return this.page.locator('.flow-run-overview-topology .topology-shell'); }

  public async inspectAcquisitionFailure(consoleUrl: string, scope: ExecutionScopeFixture,
    text: ExpectedText['acquisitionFailure']): Promise<void> {
    // Use the built-in REST Flow and real governed Tool. The managed test host
    // returns HTTP 200 text/html for this one reserved fixture URL, entirely offline.
    await new SourceLifecycleFixtures(this.page).grantBuiltinAccess(consoleUrl, scope.scopes.tenant,
      ['data-source-rest-acquisition-builtin', 'agentstration.rest.get', 'rest-builtin']);
    const name = `rejected-rest-${Date.now().toString(36)}`;
    this.scope = scope.scopes.workspace;
    await this.post(consoleUrl, '/api/datasources', {
      name, scopeRef: scope.scopes.workspace, properties: {
        displayName: name, enabled: true, profile: { name: 'rest-builtin', scopeRef: '/instance' },
        configuration: { url: 'https://acquisition-failure.example.test/rejected.html' },
      },
    }, 201);
    await this.openSource(consoleUrl, name, 'acquisitions');
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceStartAcquisition)).toBeEnabled();
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceStartAcquisition).click();
    let acquisition!: Acquisition;
    await expect.poll(async () => {
      acquisition = (await this.read<Acquisition[]>(consoleUrl, this.sourceApi(name) + '/acquisitions'))[0];
      return acquisition?.state;
    }, { timeout: 90_000 }).toBe(5);
    const message = "Media type 'text/html' is not allowed for this profile.";
    const code = 'http_media_type_invalid';
    const run = await this.read<{ status: number; error: { code: string; details: string } }>(consoleUrl,
      `/api/flowRuns/${encodeURIComponent(acquisition.flowRunId)}`);
    expect(run.status).toBe(4);
    expect(run.error).toMatchObject({ code: 'acquisition_failed', details: message });
    // Return through the acquisition history link, as an operator would.
    await this.openSource(consoleUrl, name, 'acquisitions');
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitionRow)
      .filter({ hasText: acquisition.flowRunId }).getByRole('link').click();
    await expect(this.page.getByTestId(TestIds.flowObservability.runDetails)).toBeVisible();
    const summary = this.page.getByTestId(TestIds.sourceLifecycle.runError);
    await expect(summary).toContainText('acquisition_failed');
    await expect(summary).toContainText(message);
    await expect(summary).not.toContainText('${transition.output}');
    const topology = this.failureTopology;
    await expect(topology).toBeVisible();
    const failed = topology.locator('.topology-edge[data-edge-id="fetch-failure"]');
    await expect(failed).toHaveClass(/failed/);
    await expect(failed).not.toHaveClass(/observed/);
    await expect(failed).toHaveCSS('stroke', 'rgb(228, 85, 98)');
    const marker = await failed.getAttribute('marker-end');
    expect(marker).toMatch(/^url\(#.+\)$/);
    await expect(topology.locator(`${marker!.slice(4, -1)} path`)).toHaveCSS('fill', 'rgb(228, 85, 98)');
    await expect(topology.locator('.topology-edge[data-edge-id="input-fetch"]')).toHaveClass(/observed/);
    await expect(topology.locator('.topology-edge[data-edge-id="fetch-persist"]')).toHaveClass(/inactive/);
    const legend = topology.locator('.topology-legend');
    await expect(legend).toContainText(text.errorLegend);
    await expect(legend.locator('i.failed')).toHaveCSS('border-top-color', 'rgb(228, 85, 98)');
    if ((this.page.viewportSize()?.width ?? 1440) > 900) await expect(legend).toBeVisible();
    else await expect(legend).toBeHidden(); // Existing responsive legend contract.
    // Inspect the actual fetch step: its governed code survives the Worker HTTP boundary.
    const inspectorToggle = topology.locator('button.inspector-toggle');
    const inspector = this.page.locator('.step-inspector');
    await expect(async () => {
      if (!await inspector.isVisible()) await inspectorToggle.click();
      await expect(inspector).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    await topology.getByRole('button', { name: /Acquire source content/ }).click();
    await expect(inspector).toContainText(code);
    await expect(inspector).toContainText(message);
    await expect(inspector).toContainText('fetch-failure');
    await expect(inspector).not.toContainText('${transition.output}');
    await this.assertResponsive();
    const viewport = topology.locator('.topology-viewport');
    const bounds = await viewport.boundingBox();
    expect(bounds?.width).toBeGreaterThan(200);
    const graph = topology.locator('.topology-canvas');
    await expect(graph).toHaveClass(/fit/);
    await expect(topology.locator('.topology-node')).toHaveCount(5);
    // Nodes retain their full keyboard-accessible labels at either viewport.
    await expect(topology.getByRole('button', { name: /Acquisition manifest/ })).toBeVisible();
  }

  public async administer(consoleUrl: string, scopeFixture: ExecutionScopeFixture, text: ExpectedText['sourceLifecycle']): Promise<void> {
    await this.provision(consoleUrl, scopeFixture, false);
    const f = this.fixture;
    await this.open(consoleUrl, '/data-source-profiles', TestIds.sourceLifecycle.profiles);
    await this.createProfileInConsole(consoleUrl, text);
    await this.inspectProfileYaml(consoleUrl, text);
    await this.open(consoleUrl, '/data-sources', TestIds.sourceLifecycle.sources);
    for (const [index, name] of f.sources.entries()) await this.createSourceInConsole(consoleUrl, name, index, text);
    const first = await this.acquire(consoleUrl, f.sources[0]);
    const second = await this.acquire(consoleUrl, f.sources[1]);
    expect(first.sourceConfiguration).not.toEqual(second.sourceConfiguration);
    await this.assertAcquisition(consoleUrl, first, '1.0.0');
    await this.assertAcquisition(consoleUrl, second, '1.0.0');
    await this.reviseProfile(consoleUrl, text);
    const future = await this.acquire(consoleUrl, f.sources[0]);
    await this.assertAcquisition(consoleUrl, future, '1.1.0');
    const history = await this.read<Acquisition[]>(consoleUrl, this.sourceApi(f.sources[0]) + '/acquisitions');
    expect(history.find(item => item.metadata.name === first.metadata.name)).toEqual(first);
    expect(history).toHaveLength(2);
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitionRow).filter({ hasText: '1.0.0' })).toHaveCount(1);
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitionRow).filter({ hasText: '1.1.0' })).toHaveCount(1);
    await this.unavailableProfile(consoleUrl, text);
    await this.assertScopeBoundaries(consoleUrl, scopeFixture, false);
    await this.assertUnauthorized(consoleUrl, scopeFixture, this.sourceApi(f.sources[0]));
    await this.openSource(consoleUrl, f.sources[0], 'acquisitions');
  }

  public async project(consoleUrl: string, scopeFixture: ExecutionScopeFixture, text: ExpectedText['sourceLifecycle']): Promise<void> {
    const { first, simple } = await this.projectSingleInput(consoleUrl, scopeFixture, text, false);
    const f = this.fixture;

    // A required input without an acquisition makes the source not ready.
    await this.editKnowledge(consoleUrl);
    await this.page.getByRole('button', { name: text.addDataSource, exact: true }).click();
    await this.configureBinding(1, f.sources[1], 'secondary');
    await this.saveKnowledge();
    const readiness = await this.read<{ ready: boolean; dataSources: { name: string; ready: boolean }[] }>(consoleUrl, this.knowledgeApi() + '/readiness');
    expect(readiness.ready).toBe(false);
    expect(readiness.dataSources.find(item => item.name === 'secondary')?.ready).toBe(false);
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.knowledgeDetails)).toBeVisible();
    await expect(this.page.getByText(text.notReady, { exact: true }).first()).toBeVisible();
    await this.openKnowledge(consoleUrl, 'projections');
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceStartProjection)).toBeDisabled();

    // Optional input failure is visible in provenance while the valid input publishes.
    await this.editKnowledge(consoleUrl);
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBinding).nth(1).getByRole('checkbox').uncheck();
    await this.saveKnowledge();
    const partial = await this.runProjection(consoleUrl);
    expect(partial.inputIssues).toEqual([expect.objectContaining({ bindingName: 'secondary', code: 'knowledge_projection_acquisition_missing' })]);
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.projectionProvenance)).toContainText('secondary');
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.projectionProvenance)).toContainText(partial.inputIssues[0].code);
    await this.assertProjection(consoleUrl, partial, [first]);

    const second = await this.acquire(consoleUrl, f.sources[1]);
    await this.editKnowledge(consoleUrl);
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBinding).nth(1).getByRole('checkbox').check();
    await this.saveKnowledge();
    const aggregate = await this.runProjection(consoleUrl);
    expect(aggregate.inputIssues).toEqual([]);
    await this.assertProjection(consoleUrl, aggregate, [first, second]);
    await this.retrieve(consoleUrl, aggregate);

    // Reacquisition does not publish a Snapshot or launch a projection.
    const before = await this.read<Projection[]>(consoleUrl, this.knowledgeApi() + '/projections');
    const reacquired = await this.acquire(consoleUrl, f.sources[0]);
    expect(reacquired.metadata.name).not.toBe(first.metadata.name);
    expect(await this.read<Projection[]>(consoleUrl, this.knowledgeApi() + '/projections')).toEqual(before);
    const next = await this.runProjection(consoleUrl);
    await this.assertProjection(consoleUrl, next, [reacquired, second]);
    const snapshots = await this.read<{ snapshot: Snapshot }[]>(consoleUrl, this.knowledgeApi() + '/snapshots');
    expect(snapshots.find(item => item.snapshot.metadata.name === simple.snapshotName)?.snapshot.projectionInputs[0].acquisitionId).toBe(first.metadata.name);
    expect(await this.read<Acquisition[]>(consoleUrl, this.sourceApi(f.sources[0]) + '/acquisitions')).toHaveLength(2);
    expect(await this.read<Acquisition[]>(consoleUrl, this.sourceApi(f.sources[1]) + '/acquisitions')).toHaveLength(1);
    await this.assertScopeBoundaries(consoleUrl, scopeFixture, true);
    await this.assertUnauthorized(consoleUrl, scopeFixture, this.knowledgeApi());
    await this.openKnowledge(consoleUrl, 'snapshots');
    await expect(this.snapshots).toBeVisible();
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.snapshotDetails)).toBeVisible();
  }

  /** Minimal Console preparation; multi-input readiness and history stay in project(). */
  public async prepareReadyKnowledge(consoleUrl: string, scope: ExecutionScopeFixture, text: ExpectedText['sourceLifecycle']): Promise<void> {
    await this.projectSingleInput(consoleUrl, scope, text, true);
    expect(await this.read<Acquisition[]>(consoleUrl, this.sourceApi(this.fixture.sources[0]) + '/acquisitions')).toHaveLength(1);
    expect(await this.read<Projection[]>(consoleUrl, this.knowledgeApi() + '/projections')).toHaveLength(1);
    await this.openKnowledge(consoleUrl, 'snapshots');
    await expect(this.snapshots).toBeVisible();
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.snapshotDetails)).toBeVisible();
  }

  private async projectSingleInput(consoleUrl: string, scopeFixture: ExecutionScopeFixture, text: ExpectedText['sourceLifecycle'], singleSource: boolean): Promise<{ first: Acquisition; simple: Projection }> {
    await this.provision(consoleUrl, scopeFixture, true, singleSource);
    const f = this.fixture;
    const first = await this.acquire(consoleUrl, f.sources[0]);
    await this.open(consoleUrl, '/knowledge-sources', TestIds.sourceLifecycle.knowledgeSources);
    await this.open(consoleUrl, '/knowledge-sources/new', TestIds.schemaDrivenSources.knowledgeSourceEditor);
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceDisplayName).fill(f.knowledge);
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceDisplayName).blur();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceName)).toHaveValue(f.knowledge);
    await this.page.getByLabel(text.enabled, { exact: false }).check();
    await choose(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjectionFlow), f.projectionFlow);
    await choose(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceRetrievalFlow), 'knowledge-retrieval-builtin');
    await this.configureBinding(0, f.sources[0], 'primary');
    await this.saveKnowledge();
    const simple = await this.runProjection(consoleUrl);
    await this.assertProjection(consoleUrl, simple, [first]);
    await this.retrieve(consoleUrl, simple);

    return { first, simple };
  }

  private async provision(consoleUrl: string, scope: ExecutionScopeFixture, sources: boolean, singleSource = false): Promise<void> {
    this.page.setDefaultTimeout(15_000);
    const suffix = Date.now().toString(36);
    const fixtures = new SourceLifecycleFixtures(this.page);
    await fixtures.grantBuiltinAccess(consoleUrl, scope.scopes.tenant);
    const artifactId = await fixtures.createDurableArtifact(consoleUrl, suffix);
    const artifactIds = [artifactId];
    if (!singleSource) artifactIds.push(await fixtures.createDurableArtifact(consoleUrl, `${suffix}-secondary`));
    this.fixture = {
      scope: scope.scopes.workspace, artifactId, artifactIds,
      profile: `lifecycle-profile-${suffix}`, acquisitionFlow: `lifecycle-acquire-${suffix}`,
      projectionFlow: `lifecycle-project-${suffix}`, sources: [`lifecycle-alpha-${suffix}`, `lifecycle-beta-${suffix}`].slice(0, singleSource ? 1 : 2),
      knowledge: `lifecycle-knowledge-${suffix}`,
    };
    const f = this.fixture;
    this.scope = f.scope;
    await fixtures.createPublishedFlow(consoleUrl, f.acquisitionFlow, f.acquisitionFlow, 'datasource.acquisition/v1', acquisitionFlowSource('${input.sourceConfiguration.artifactId}'));
    await fixtures.createPublishedFlow(consoleUrl, f.projectionFlow, f.projectionFlow, 'knowledge.projection/v1', projectionFlowSource());
    if (sources) {
      await this.post(consoleUrl, '/api/datasourceprofiles', {
        name: f.profile, scopeRef: f.scope, properties: this.profileProperties(),
      }, 201);
      await this.post(consoleUrl, `/api/datasourceprofiles/${f.profile}/revisions`, { version: '1.0.0', activate: true }, 201);
      for (const [index, name] of f.sources.entries()) await this.post(consoleUrl, '/api/datasources', {
        name, scopeRef: f.scope, properties: this.sourceProperties(index),
      }, 201);
    }
  }

  private profileProperties(): object {
    return {
      displayName: this.fixture.profile, enabled: true, version: '1.0.0',
      acquisitionFlow: { name: this.fixture.acquisitionFlow, scopeRef: this.fixture.scope, version: '1.0.0', useActiveVersion: false },
      configurationSchema: sourceSchema, toolBindings: [], limits: {}, policies: {},
    };
  }
  private sourceProperties(index: number): object {
    return {
      displayName: this.fixture.sources[index], enabled: true,
      profile: { name: this.fixture.profile, scopeRef: this.fixture.scope },
      configuration: { endpoint: `https://example.test/lifecycle/${index}`, artifactId: this.fixture.artifactIds[index] },
    };
  }

  private async createProfileInConsole(consoleUrl: string, text: ExpectedText['sourceLifecycle']): Promise<void> {
    const f = this.fixture;
    await this.open(consoleUrl, '/data-source-profiles/new', TestIds.sourceLifecycle.profileEditor);
    await this.page.getByLabel(text.displayName, { exact: true }).fill(f.profile);
    await this.page.getByLabel(text.displayName, { exact: true }).blur();
    await expect(this.page.getByRole('textbox', { name: text.technicalName, exact: false })).toHaveValue(f.profile);
    await choose(this.page.getByRole('combobox', { name: text.acquisitionFlow, exact: true }), f.acquisitionFlow);
    await this.page.getByLabel(text.configurationSchema, { exact: true }).fill(JSON.stringify(sourceSchema));
    await this.page.getByRole('button', { name: text.save, exact: true }).click();
    await expect(this.page).toHaveURL(new RegExp(`/data-source-profiles/${f.profile}\\?`));
    const profile = await this.read<ResourceWire & { activeVersion: string }>(consoleUrl, `/api/datasourceprofiles/${f.profile}`);
    expect(profile.activeVersion).toBe('1.0.0');
  }

  private async inspectProfileYaml(consoleUrl: string, text: ExpectedText['sourceLifecycle']): Promise<void> {
    await this.open(consoleUrl, `/data-source-profiles/${this.fixture.profile}`, TestIds.sourceLifecycle.profileEditor);
    await this.page.getByRole('tab', { name: text.yaml, exact: true }).click();
    await expect(this.page.locator('textarea.runner-json')).toHaveValue(new RegExp(this.fixture.acquisitionFlow));
    await this.page.getByRole('tab', { name: text.revisions, exact: true }).click();
    await expect(this.page.getByRole('row').filter({ hasText: '1.0.0' })).toHaveCount(1);
  }

  private async createSourceInConsole(consoleUrl: string, name: string, index: number, text: ExpectedText['sourceLifecycle']): Promise<void> {
    await this.open(consoleUrl, '/data-sources/new', TestIds.schemaDrivenSources.dataSourceEditor);
    await this.page.getByLabel(text.displayName, { exact: true }).fill(name);
    await this.page.getByLabel(text.displayName, { exact: true }).blur();
    await expect(this.page.getByRole('textbox', { name: text.technicalName, exact: false })).toHaveValue(name);
    await choose(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceProfile), this.fixture.profile);
    const editor = this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceConfiguration);
    const endpoint = editor.locator('[data-schema-path="$.endpoint"] input');
    await endpoint.fill('x');
    await endpoint.blur();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeDisabled();
    await editor.locator('[data-schema-path="$.artifactId"] input').fill(this.fixture.artifactIds[index]);
    await endpoint.fill(`https://example.test/lifecycle/${index}`);
    await endpoint.blur();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeEnabled();
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave).click();
    await expect(this.page).toHaveURL(new RegExp(`/data-sources/${name}\\?`));
    await this.openSource(consoleUrl, name, 'yaml');
    await expect(this.page.locator('textarea.runner-json')).toHaveValue(new RegExp(`https://example.test/lifecycle/${index}`));
  }

  private async reviseProfile(consoleUrl: string, text: ExpectedText['sourceLifecycle']): Promise<void> {
    const f = this.fixture;
    await this.open(consoleUrl, `/data-source-profiles/${f.profile}`, TestIds.sourceLifecycle.profileEditor);
    await this.page.getByRole('tab', { name: text.definition, exact: true }).click();
    await this.page.getByLabel(text.version, { exact: true }).fill('1.1.0');
    await this.page.getByRole('button', { name: text.save, exact: true }).click();
    await expect.poll(async () => (await this.read<ResourceWire>(consoleUrl, `/api/datasourceprofiles/${f.profile}`)).definition.version).toBe('1.1.0');
    await this.page.getByRole('tab', { name: text.revisions, exact: true }).click();
    await this.page.getByLabel(text.version, { exact: true }).fill('1.1.0');
    await this.page.getByRole('button', { name: text.publish, exact: true }).click();
    await expect(this.page.getByRole('row').filter({ hasText: '1.1.0' })).toBeVisible();
    // Explicit activation and rollback exercise the revision controls as well.
    await this.page.getByRole('row').filter({ hasText: '1.0.0' }).getByRole('button', { name: text.activate, exact: true }).click();
    await expect(this.page.getByRole('row').filter({ hasText: '1.1.0' }).getByRole('button', { name: text.activate, exact: true })).toBeVisible();
    await this.page.getByRole('row').filter({ hasText: '1.1.0' }).getByRole('button', { name: text.activate, exact: true }).click();
    await expect.poll(async () => (await this.read<ResourceWire & { activeVersion: string }>(consoleUrl, `/api/datasourceprofiles/${f.profile}`)).activeVersion).toBe('1.1.0');
  }

  private async unavailableProfile(consoleUrl: string, text: ExpectedText['sourceLifecycle']): Promise<void> {
    const path = `/api/datasourceprofiles/${this.fixture.profile}`;
    const current = await this.get(consoleUrl, path);
    const profile = await current.json() as ResourceWire;
    await this.put(consoleUrl, path, { properties: { ...profile.definition, enabled: false } }, current.headers()['etag']);
    try {
      await this.openSource(consoleUrl, this.fixture.sources[0], 'acquisitions');
      await this.fillParameters(TestIds.schemaDrivenSources.dataSourceAcquisitionParameters, { query: 'lifecycle', limit: 2 });
      await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceStartAcquisition)).toBeDisabled();
      const readiness = await this.read<{ ready: boolean; issues: string[] }>(consoleUrl, this.sourceApi(this.fixture.sources[0]) + '/readiness');
      expect(readiness.ready).toBe(false);
      expect(readiness.issues.length).toBeGreaterThan(0);
      await this.openSource(consoleUrl, this.fixture.sources[0], 'overview');
      await expect(this.page.getByText(text.notReady, { exact: true }).first()).toBeVisible();
    } finally {
      const latest = await this.get(consoleUrl, path);
      await this.put(consoleUrl, path, { properties: profile.definition }, latest.headers()['etag']);
    }
  }

  private async acquire(consoleUrl: string, name: string): Promise<Acquisition> {
    const before = await this.read<Acquisition[]>(consoleUrl, this.sourceApi(name) + '/acquisitions');
    await this.openSource(consoleUrl, name, 'acquisitions');
    await this.fillParameters(TestIds.schemaDrivenSources.dataSourceAcquisitionParameters, { query: 'lifecycle', limit: 2 });
    await this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceStartAcquisition).click();
    let result!: Acquisition;
    await expect.poll(async () => {
      const history = await this.read<Acquisition[]>(consoleUrl, this.sourceApi(name) + '/acquisitions');
      result = history.find(item => !before.some(old => old.metadata.name === item.metadata.name))!;
      return result !== undefined && result.state >= 4;
    }, { timeout: 90_000 }).toBe(true);
    expect(result.state, result.errorMessage ?? undefined).toBe(4);
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitionRow).filter({ hasText: result.flowRunId }))
      .toHaveAttribute('data-state', 'Succeeded', { timeout: 15_000 });
    return result;
  }

  private async assertAcquisition(consoleUrl: string, acquisition: Acquisition, version: string): Promise<void> {
    const revision = await this.read<{ definitionHash: string; dependencyHash: string; acquisitionFlow: Acquisition['composition']['flow']; toolBindings: unknown[] }>(
      consoleUrl, `/api/datasourceprofiles/${this.fixture.profile}/revisions/${version}`);
    expect(acquisition.composition.profile.definitionHash).toBe(revision.definitionHash);
    expect(acquisition.composition.profile.dependencyHash).toBe(revision.dependencyHash);
    expect(acquisition.composition.flow).toEqual(revision.acquisitionFlow);
    expect(acquisition.composition.tools).toEqual(revision.toolBindings);
    expect(acquisition.composition.profile).toMatchObject({ name: this.fixture.profile, version });
    expect(acquisition.composition.profile.definitionHash).toMatch(/^[a-f0-9]{64}$/i);
    expect(acquisition.composition.profile.dependencyHash).toMatch(/^[a-f0-9]{64}$/i);
    expect(acquisition.composition.flow).toMatchObject({ name: this.fixture.acquisitionFlow, version: '1.0.0' });
    expect(acquisition.composition.tools).toEqual([]); // The bounded fixture deliberately invokes no Tools.
    expect(acquisition.manifest.artifacts.map(item => item.artifactId)).toEqual([acquisition.sourceConfiguration.artifactId]);
  }

  private async configureBinding(index: number, source: string, name: string): Promise<void> {
    const binding = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBinding).nth(index);
    await binding.getByRole('textbox').first().fill(name);
    await choose(binding.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBindingDataSource), source);
  }
  private async saveKnowledge(): Promise<void> {
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceSave).click();
    await expect(this.page).toHaveURL(new RegExp(`/knowledge-sources/${this.fixture.knowledge}(?:\\?|$)`));
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.knowledgeDetails)).toBeVisible();
  }
  private async editKnowledge(consoleUrl: string): Promise<void> {
    await this.open(consoleUrl, `/knowledge-sources/${this.fixture.knowledge}/edit`, TestIds.schemaDrivenSources.knowledgeSourceEditor);
  }
  private async runProjection(consoleUrl: string): Promise<Projection> {
    const before = await this.read<Projection[]>(consoleUrl, this.knowledgeApi() + '/projections');
    await this.openKnowledge(consoleUrl, 'projections');
    await this.fillParameters(TestIds.schemaDrivenSources.knowledgeSourceProjectionParameters, { audience: 'operators', includeMetadata: true });
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceStartProjection).click();
    let result!: Projection;
    await expect.poll(async () => {
      const history = await this.read<Projection[]>(consoleUrl, this.knowledgeApi() + '/projections');
      result = history.find(item => !before.some(old => old.metadata.name === item.metadata.name))!;
      return result !== undefined && result.state >= 4;
    }, { timeout: 90_000 }).toBe(true);
    expect(result.state, result.errorMessage ?? undefined).toBe(4);
    expect(result.snapshotName).toBeTruthy();
    await this.openKnowledge(consoleUrl, 'projections');
    await this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjectionRow).locator(`button[title="${result.metadata.name}"]`).click();
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.projectionProvenance)).toBeVisible();
    return result;
  }

  private async assertProjection(consoleUrl: string, projection: Projection, acquisitions: Acquisition[]): Promise<void> {
    expect(projection.projectionFlow).toMatchObject({ name: this.fixture.projectionFlow, version: '1.0.0' });
    expect(projection.retrievalFlow).toMatchObject({ name: 'knowledge-retrieval-builtin', version: '1.0.0' });
    expect(projection.inputs.map(item => item.acquisitionId).sort()).toEqual(acquisitions.map(item => item.metadata.name).sort());
    for (const acquisition of acquisitions) {
      const input = projection.inputs.find(item => item.acquisitionId === acquisition.metadata.name)!;
      // Embedded JsonElement evidence retains .NET property casing; compare the
      // complete composition after normalizing property names, preserving values.
      expect(camelCaseEvidence(input.acquisitionComposition)).toEqual(acquisition.composition);
      expect(input.acquiredArtifacts.map(item => item.artifactId)).toEqual(acquisition.manifest.artifacts.map(item => item.artifactId));
      expect(input.preparedArtifacts.map(item => item.artifactId)).toEqual(acquisition.manifest.artifacts.map(item => item.artifactId));
    }
    const snapshots = await this.read<{ snapshot: Snapshot }[]>(consoleUrl, this.knowledgeApi() + '/snapshots');
    const snapshot = snapshots.find(item => item.snapshot.metadata.name === projection.snapshotName)!.snapshot;
    expect(snapshot.projectionId).toBe(projection.metadata.name);
    expect(snapshot.projectionInputs).toEqual(projection.inputs);
    expect(snapshot.projectionFlow).toEqual(projection.projectionFlow);
    expect(snapshot.retrievalFlow).toEqual(projection.retrievalFlow);
    const expectedArtifacts = acquisitions.flatMap(item => item.manifest.artifacts.map(artifact => artifact.artifactId));
    expect(snapshot.artifacts.map(item => item.artifactId).sort()).toEqual(expectedArtifacts.sort());
    for (const artifact of snapshot.artifacts) {
      expect(expectedArtifacts).toContain(artifact.artifactId);
      expect(artifact.storageFlowRunId).toBeTruthy();
      expect(artifact.sha256).toMatch(/^[a-f0-9]{64}$/i);
      expect(artifact.length).toBeGreaterThan(0);
    }
    await this.openKnowledge(consoleUrl, 'snapshots');
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.snapshots)).toContainText(snapshot.metadata.name);
    await this.page.getByTestId(TestIds.sourceLifecycle.snapshots).locator(`button[title="${snapshot.metadata.name}"]`).click();
    await expect(this.page.getByTestId(TestIds.sourceLifecycle.snapshotDetails).locator(`a[title="${this.fixture.artifactId}"]`).first()).toBeVisible();
  }

  private async retrieve(consoleUrl: string, projection: Projection): Promise<void> {
    await this.openKnowledge(consoleUrl, 'retrieval');
    await this.page.getByTestId(TestIds.sourceLifecycle.retrievalText).fill('Schema-driven');
    await this.page.getByTestId(TestIds.sourceLifecycle.retrievalText).blur();
    await this.page.getByTestId(TestIds.sourceLifecycle.retrievalRun).click();
    const result = this.page.getByTestId(TestIds.sourceLifecycle.retrievalResult);
    await expect(result).toContainText('Schema-driven source');
    await expect(result).toContainText(projection.snapshotName!);
    for (const input of projection.inputs) {
      for (const artifact of input.acquiredArtifacts) await expect(result).toContainText(artifact.artifactId);
    }
  }

  private async assertScopeBoundaries(consoleUrl: string, scope: ExecutionScopeFixture, knowledge: boolean): Promise<void> {
    const f = this.fixture;
    // Reuse a published protected ancestor profile and its existing grants. Binding
    // resolution is checked separately from provider execution (which stays offline).
    const profiles = await this.read<(ResourceWire & { scopeRef: string; activeVersion: string })[]>(consoleUrl, '/api/datasourceprofiles');
    const broader = profiles.find(item => item.scopeRef === '/instance' && item.metadata.name === 'artifact-import-builtin' && item.activeVersion);
    expect(broader, 'A bootstrap instance Data Source Profile is visible to the Workspace').toBeDefined();
    const allowed = await this.page.request.post(this.apiUrl(consoleUrl, '/api/datasources'), {
      data: { name: `${f.sources[0]}-inherited`, scopeRef: f.scope, properties: {
        displayName: 'Inherited profile', enabled: true, profile: { name: broader!.metadata.name, scopeRef: '/instance' },
        configuration: { artifactIds: [f.artifactId] },
      } },
    });
    await status(allowed, 201);
    for (const target of [`/workspaces/${scope.siblingWorkspaceId}`, `/workspaces/${scope.siblingTenantWorkspaceId}`]) {
      const ownerHeaders = { 'X-Agentstration-Context': target };
      const inheritedProperties = {
        displayName: 'Lateral fixture', enabled: true,
        profile: { name: broader!.metadata.name, scopeRef: '/instance' },
        configuration: { artifactIds: [f.artifactId] },
      };
      // A real sibling resource is required: an absent name only proves lookup failure.
      if (target === `/workspaces/${scope.siblingWorkspaceId}`) {
        if (knowledge) {
          await status(await this.page.request.post(`${consoleUrl}/api/datasources`, {
            headers: ownerHeaders, data: { name: f.sources[0], scopeRef: target, properties: inheritedProperties },
          }), 201);
        } else {
          await new SourceLifecycleFixtures(this.page).createPublishedFlow(consoleUrl, f.acquisitionFlow,
            f.acquisitionFlow, 'datasource.acquisition/v1', acquisitionFlowSource(f.artifactId), target);
          await status(await this.page.request.post(`${consoleUrl}/api/datasourceprofiles`, {
            headers: ownerHeaders, data: { name: f.profile, scopeRef: target, properties: {
              ...this.profileProperties(), acquisitionFlow: { name: f.acquisitionFlow, scopeRef: target, version: '1.0.0', useActiveVersion: false },
            } },
          }), 201);
          await status(await this.page.request.post(`${consoleUrl}/api/datasourceprofiles/${f.profile}/revisions?scopeRef=${encodeURIComponent(target)}`, {
            headers: ownerHeaders, data: { version: '1.0.0', activate: true },
          }), 201);
        }
      }
      const path = knowledge ? '/api/knowledgesources' : '/api/datasources';
      const properties = knowledge ? this.knowledgeProperties(f.sources[0], target) : {
        ...this.sourceProperties(0), profile: { name: f.profile, scopeRef: target },
      };
      const response = await this.page.request.post(this.apiUrl(consoleUrl, path), {
        data: { name: `${f.sources[0]}-denied`, scopeRef: f.scope, properties },
      });
      await status(response, 422);
      const problem = await response.json() as { type: string };
      expect(problem.type).toMatch(/scope|profile|data.source|input/);
    }
    if (knowledge) {
      const ancestorName = `${f.sources[0]}-ancestor`;
      const created = await this.page.request.post(`${consoleUrl}/api/datasources`, {
        headers: { 'X-Agentstration-Context': '/instance' },
        data: { name: ancestorName, scopeRef: '/instance', properties: {
          displayName: ancestorName, enabled: true,
          profile: { name: broader!.metadata.name, scopeRef: '/instance' }, configuration: { artifactIds: [f.artifactId] },
        } },
      });
      await status(created, 201);
      const ancestor = await created.json() as ResourceWire;
      await status(await this.page.request.put(`${consoleUrl}/api/resource-access-policies/${ancestor.uid}`, {
        headers: { 'X-Agentstration-Context': '/instance' },
        data: { resource: { uid: ancestor.uid, ownerScopeRef: '/instance', namespace: 'default', kind: 'DataSource', generation: ancestor.generation },
          grants: [{ targetScopeRef: f.scope, actions: ['discover', 'use'], includeDescendants: false }] },
      }), 200);
      await this.post(consoleUrl, '/api/knowledgesources', {
        name: `${f.knowledge}-ancestor`, scopeRef: f.scope, properties: this.knowledgeProperties(ancestorName, '/instance'),
      }, 201);
      await this.open(consoleUrl, `/knowledge-sources/${f.knowledge}-ancestor/edit`, TestIds.schemaDrivenSources.knowledgeSourceEditor);
      await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBindingDataSource).locator('option:checked')).toContainText(ancestorName);
    }
    if (knowledge) {
      const elevated = await this.page.request.post(this.apiUrl(consoleUrl, '/api/knowledgesources'), {
        headers: { 'X-Agentstration-Context': '/instance' },
        data: { name: `${f.knowledge}-elevated`, scopeRef: '/instance', properties: this.knowledgeProperties(f.sources[0], f.scope) },
      });
      await status(elevated, 422);
    }
  }
  private knowledgeProperties(source: string, scopeRef: string): object {
    return { displayName: 'Scope denial fixture', enabled: true,
      dataSources: [{ name: 'origin', dataSource: { name: source, scopeRef }, required: true }],
      projectionFlow: { name: 'knowledge-projection-builtin', scopeRef: '/instance', version: '1.0.0', useActiveVersion: false },
      retrievalFlow: { name: 'knowledge-retrieval-builtin', scopeRef: '/instance', version: '1.0.0', useActiveVersion: false },
    };
  }

  private async assertUnauthorized(consoleUrl: string, scope: ExecutionScopeFixture, resource: string): Promise<void> {
    const userName = `source-member-${Date.now().toString(36)}`;
    const password = 'Source-Member-812!'; // Disposable local browser fixture.
    await this.post(consoleUrl, '/api/identity/accounts', {
      userName, password, displayName: 'Source lifecycle member', email: null,
      workspaceId: scope.siblingTenantWorkspaceId, role: 'Member',
    }, 201);
    const browser = this.page.context().browser();
    if (!browser) throw new Error('A browser is required for the authorization fixture.');
    const context = await browser.newContext();
    try {
      const member = await context.newPage();
      await new LoginPage(member).signIn(consoleUrl, userName, password);
      const identityResponse = await member.request.get(`${consoleUrl}/api/identity/context`);
      await status(identityResponse, 200);
      const identity = await identityResponse.json() as { context: { tenantId: string; workspaceId: string } };
      expect(identity.context.tenantId).toBe(scope.siblingTenantId);
      expect(identity.context.workspaceId).toBe(scope.siblingTenantWorkspaceId);
      const denied = await member.request.get(this.apiUrl(consoleUrl, resource));
      await status(denied, 403);
    } finally { await context.close(); }
  }

  public async openSnapshotArtifact(consoleUrl: string): Promise<void> {
    await this.openKnowledge(consoleUrl, 'snapshots');
    await this.page.getByTestId(TestIds.sourceLifecycle.snapshotDetails)
      .getByRole('link', { name: this.fixture.artifactId, exact: true }).click();
    await expect(this.page.getByTestId(TestIds.artifacts.durableDetails)).toBeVisible();
  }

  public async snapshotEvidence(consoleUrl: string): Promise<unknown> {
    return await this.read(consoleUrl, this.knowledgeApi() + '/snapshots');
  }

  public async assertSnapshotEvidence(consoleUrl: string, expected: unknown): Promise<void> {
    expect(await this.snapshotEvidence(consoleUrl)).toEqual(expected);
  }

  public async assertResponsive(): Promise<void> {
    const widths = await this.page.evaluate(async () => {
      await document.fonts.ready;
      return {
        actual: document.documentElement.scrollWidth,
        viewport: window.innerWidth,
        overflowing: Array.from(document.querySelectorAll<HTMLElement>('main *')).map(element => {
          const bounds = element.getBoundingClientRect();
          return {
            element: element.dataset.testid ?? `${element.tagName.toLowerCase()}.${element.className}`,
            right: Math.round(bounds.right), width: Math.round(bounds.width),
          };
        }).filter(element => element.right > window.innerWidth + 1).slice(-15),
      };
    });
    expect(widths.actual, JSON.stringify(widths.overflowing)).toBeLessThanOrEqual(widths.viewport);
  }
  private async fillParameters(testId: string, value: object): Promise<void> {
    const editor = this.page.getByTestId(testId);
    await editor.getByTestId(TestIds.common.schemaRawMode).click();
    await editor.locator('textarea.runner-json').fill(JSON.stringify(value));
    await editor.locator('textarea.runner-json').blur();
  }
  private sourceApi(name: string): string { return `/api/datasources/${name}`; }
  private knowledgeApi(): string { return `/api/knowledgesources/${this.fixture.knowledge}`; }
  private apiUrl(consoleUrl: string, path: string): string {
    return `${consoleUrl}${path}?namespace=default&scopeRef=${encodeURIComponent(this.scope)}`;
  }
  private async get(consoleUrl: string, path: string): Promise<APIResponse> {
    const response = await this.page.request.get(this.apiUrl(consoleUrl, path));
    await status(response, 200);
    return response;
  }
  private async read<T>(consoleUrl: string, path: string): Promise<T> { return await (await this.get(consoleUrl, path)).json() as T; }
  private async post(consoleUrl: string, path: string, data: object, expected: number): Promise<void> {
    await status(await this.page.request.post(this.apiUrl(consoleUrl, path), { data }), expected);
  }
  private async put(consoleUrl: string, path: string, data: object, etag: string): Promise<void> {
    await status(await this.page.request.put(this.apiUrl(consoleUrl, path), { data, headers: { 'If-Match': etag } }), 200);
  }
  private async openSource(consoleUrl: string, name: string, view: string): Promise<void> {
    await this.open(consoleUrl, `/data-sources/${name}`, TestIds.schemaDrivenSources.dataSourceEditor, view);
  }
  private async openKnowledge(consoleUrl: string, view: string): Promise<void> {
    await this.open(consoleUrl, `/knowledge-sources/${this.fixture.knowledge}`, TestIds.sourceLifecycle.knowledgeDetails, view);
  }
  private async open(consoleUrl: string, path: string, marker: string, view?: string): Promise<void> {
    const url = new URL(path, consoleUrl);
    url.searchParams.set('namespace', 'default');
    url.searchParams.set('scopeRef', this.scope);
    url.searchParams.set('context', this.scope);
    if (view) url.searchParams.set('view', view);
    const response = await this.page.goto(url.toString(), { waitUntil: 'domcontentloaded' });
    expect(response?.status()).toBe(200);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
    await expect(this.page.getByTestId(TestIds.console.shell)).toHaveAttribute('data-preferences-ready', 'true');
  }
}

const sourceSchema = {
  type: 'object', properties: { endpoint: { type: 'string', title: 'Endpoint', format: 'uri', minLength: 8 },
    artifactId: { type: 'string', title: 'Artifact', minLength: 32 } },
  required: ['endpoint', 'artifactId'], additionalProperties: false,
};
async function choose(select: Locator, name: string): Promise<void> {
  const option = select.locator('option').filter({ hasText: name }).first();
  await option.waitFor({ state: 'attached' });
  const value = await option.getAttribute('value');
  if (!value) throw new Error(`No selectable option for '${name}'.`);
  await select.selectOption(value);
}
async function status(response: APIResponse, expected: number): Promise<void> {
  expect(response.status(), `${response.url()}: ${await response.text()}`).toBe(expected);
}

function camelCaseEvidence(value: unknown): unknown {
  if (Array.isArray(value)) return value.map(camelCaseEvidence);
  if (value === null || typeof value !== 'object') return value;
  return Object.fromEntries(Object.entries(value).map(([key, item]) =>
    [key.charAt(0).toLowerCase() + key.slice(1), camelCaseEvidence(item)]));
}
