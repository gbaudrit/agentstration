import { expect, type APIResponse, type Locator, type Page } from '@playwright/test';
import { SourceLifecycleFixtures, acquisitionFlowSource, transformationFlowSource, projectionFlowSource, configurationSchema } from '../fixtures/source-lifecycle.js';
import { TestIds } from '../contracts/test-ids.js';

interface DataSourceWire {
  definition: { configuration: Record<string, unknown> };
}

interface AcquisitionWire {
  state: number | string;
  parameters: Record<string, unknown>;
  manifest?: { artifacts?: unknown[] } | null;
}

interface KnowledgeSourceWire {
  definition: {
    dataSources: { configuration: Record<string, unknown> }[];
  };
}

interface ProjectionWire {
  state: number | string;
  parameters: Record<string, unknown>;
  snapshotName?: string | null;
  errorMessage?: string | null;
}

export interface SchemaDrivenSourceFixture {
  dataSourceName: string;
  knowledgeSourceName: string;
}

export class SchemaDrivenSourcesPage {
  public lastFixture?: SchemaDrivenSourceFixture;
  private fixtureScopeRef?: string;

  public constructor(private readonly page: Page) {}

  public async provisionAndExercise(consoleUrl: string): Promise<SchemaDrivenSourceFixture> {
    this.page.setDefaultTimeout(15_000);
    const identity = await this.read<{ context: { tenantId: string } }>(`${consoleUrl}/api/identity/context`);
    await new SourceLifecycleFixtures(this.page).grantBuiltinAccess(consoleUrl, `/tenants/${identity.context.tenantId}`);
    const suffix = Date.now().toString(36);
    const workspaceId = await this.workspaceId();
    const scopeRef = `/workspaces/${workspaceId}`;
    this.fixtureScopeRef = scopeRef;
    const durableArtifactId = await new SourceLifecycleFixtures(this.page).createDurableArtifact(consoleUrl, suffix);
    const acquisitionFlow = `schema-input-acquisition-${suffix}`;
    const transformationFlow = `schema-input-transform-${suffix}`;
    const projectionFlow = `schema-input-projection-${suffix}`;
    const profileName = `schema-input-profile-${suffix}`;
    const fallbackProfileName = `schema-input-fallback-${suffix}`;
    const dataSourceName = `schema-input-source-${suffix}`;
    const knowledgeSourceName = `schema-input-knowledge-${suffix}`;

    await new SourceLifecycleFixtures(this.page).createPublishedFlow(consoleUrl, acquisitionFlow, 'Schema input acquisition', 'datasource.acquisition/v1',
      acquisitionFlowSource(durableArtifactId));
    await new SourceLifecycleFixtures(this.page).createPublishedFlow(consoleUrl, transformationFlow, 'Schema input transformation', 'artifact.transform/v1',
      transformationFlowSource());
    await new SourceLifecycleFixtures(this.page).createPublishedFlow(consoleUrl, projectionFlow, 'Schema input projection', 'knowledge.projection/v1',
      projectionFlowSource());
    await this.createProfile(consoleUrl, scopeRef, profileName, 'Schema input profile', acquisitionFlow, configurationSchema());
    await this.createProfile(consoleUrl, scopeRef, fallbackProfileName, 'Schema input fallback', acquisitionFlow,
      { type: 'object' });

    await this.createDataSource(consoleUrl, scopeRef, profileName, 'Schema input profile', 'Schema input fallback', dataSourceName);
    const source = await this.read<DataSourceWire>(`${consoleUrl}/api/datasources/${dataSourceName}?namespace=default&scopeRef=${encodeURIComponent(scopeRef)}`);
    expect(source.definition.configuration).toEqual({
      endpoint: 'https://example.test/schema-inputs', mode: 'full', options: { retries: 4 },
    });

    await this.runAcquisition(consoleUrl, scopeRef, dataSourceName);
    await this.createKnowledgeSource(consoleUrl, scopeRef, dataSourceName, knowledgeSourceName, transformationFlow, projectionFlow);
    const knowledge = await this.read<KnowledgeSourceWire>(`${consoleUrl}/api/knowledgesources/${knowledgeSourceName}?namespace=default`);
    expect(knowledge.definition.dataSources[0]?.configuration).toEqual({ prefix: 'docs', mode: 'compact' });
    await this.verifyKnowledgeEdit(consoleUrl, knowledgeSourceName);
    await this.runProjection(consoleUrl, knowledgeSourceName);

    this.lastFixture = { dataSourceName, knowledgeSourceName };
    return this.lastFixture;
  }

  public async assertLocalizedEditorModes(consoleUrl: string, dataSourceName: string,
    expected: { formMode: string; rawMode: string }): Promise<void> {
    await this.open(`${consoleUrl}/data-sources/${dataSourceName}?namespace=default&scopeRef=${encodeURIComponent(await this.scopeRef())}&view=definition`,
      TestIds.schemaDrivenSources.dataSourceEditor);
    const editor = this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceConfiguration);
    await expect(editor.getByTestId(TestIds.common.schemaFormMode)).toHaveText(expected.formMode);
    await expect(editor.getByTestId(TestIds.common.schemaRawMode)).toHaveText(expected.rawMode);
  }

  public async assertResponsive(): Promise<void> {
    const layout = await this.page.evaluate(() => ({
      scrollWidth: document.documentElement.scrollWidth,
      viewportWidth: window.innerWidth,
      offenders: Array.from(document.querySelectorAll<HTMLElement>('body *')).map(element => {
        const bounds = element.getBoundingClientRect();
        return {
          selector: `${element.tagName.toLowerCase()}${element.dataset.testid ? `[data-testid="${element.dataset.testid}"]` : ''}${element.className ? `.${String(element.className).trim().replaceAll(' ', '.')}` : ''}`,
          left: Math.round(bounds.left), right: Math.round(bounds.right), width: Math.round(bounds.width),
        };
      }).filter(item => item.right > window.innerWidth + 1 || item.width > window.innerWidth + 1).slice(-12),
    }));
    expect(layout.scrollWidth, `Overflowing elements: ${JSON.stringify(layout.offenders)}`).toBeLessThanOrEqual(layout.viewportWidth);
  }

  private async createDataSource(consoleUrl: string, scopeRef: string, profileTechnicalName: string,
    profileName: string, fallbackProfileName: string, dataSourceName: string): Promise<void> {
    await expectApi(await this.page.request.post(`${consoleUrl}/api/datasources`, {
      data: {
        name: dataSourceName, namespace: 'default', scopeRef,
        properties: {
          displayName: dataSourceName, enabled: true,
          profile: { name: profileTechnicalName, namespace: 'default', scopeRef },
          configuration: { endpoint: 'https://example.test/schema-inputs', mode: 'full', options: { retries: 4 } },
        },
      },
    }), 201, `create Data Source '${dataSourceName}'`);
    await this.open(`${consoleUrl}/data-sources/${dataSourceName}?namespace=default&scopeRef=${encodeURIComponent(scopeRef)}&view=definition`,
      TestIds.schemaDrivenSources.dataSourceEditor);
    const profile = this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceProfile);
    const persisted = this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceConfiguration);
    const endpoint = field(persisted, '$.endpoint').locator('input');
    await expect(endpoint).toHaveValue('https://example.test/schema-inputs');
    await expect(endpoint).toHaveAttribute('type', 'url');
    await expect(endpoint).toHaveAttribute('minlength', '8');
    const dataSourceEditor = persisted.getByTestId(TestIds.common.schemaInputEditor);
    const dataSourceRaw = await switchToRaw(dataSourceEditor);
    await dataSourceRaw.fill(JSON.stringify({ endpoint: 'x', mode: 'full', options: { retries: 4 } }, null, 2));
    await expect(dataSourceRaw).toHaveValue(/"endpoint": "x"/);
    await expect(dataSourceEditor.getByRole('alert').first()).toBeVisible();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeDisabled();
    await dataSourceRaw.fill(JSON.stringify({
      endpoint: 'https://example.test/schema-inputs', mode: 'full', options: { retries: 4 },
    }, null, 2));
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceSave)).toBeEnabled();
    await dataSourceEditor.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(endpoint).toHaveValue('https://example.test/schema-inputs');
    const modeField = field(persisted, '$.mode');
    const modeRules = modeField.locator('[tabindex="0"]');
    const mode = modeField.locator('select');
    await endpoint.focus();
    await expect(endpoint).toBeFocused();
    await this.page.keyboard.press('ControlOrMeta+A');
    await endpoint.pressSequentially('https://example.test/schema-inputs', { delay: 25 });
    await expect(endpoint).toHaveValue('https://example.test/schema-inputs');
    await this.page.keyboard.press('Tab');
    await expect(modeRules).toBeFocused();
    await this.page.keyboard.press('Tab');
    await expect(mode).toBeFocused();
    await mode.selectOption({ label: 'full' });
    await field(persisted, '$.options.retries').locator('input').fill('4');
    await this.assertRawRoundTrip(persisted, [
      '"endpoint": "https://example.test/schema-inputs"', '"mode": "full"', '"retries": 4',
    ]);

    await selectContaining(profile, fallbackProfileName);
    await expect(profile).toHaveValue(/schema-input-fallback/);
    const fallbackRaw = persisted.getByTestId(TestIds.common.schemaInputEditor).locator('textarea.runner-json');
    await expect(fallbackRaw).toHaveValue(/https:\/\/example\.test\/schema-inputs/);
    await expect(persisted.getByRole('status')).toBeVisible();
    await selectContaining(profile, profileName);
    await expect(profile).toHaveValue(/schema-input-profile/);
    await expect(field(persisted, '$.endpoint').locator('input')).toHaveValue('https://example.test/schema-inputs');
  }

  private async runAcquisition(consoleUrl: string, scopeRef: string, dataSourceName: string): Promise<void> {
    const readiness = await this.read<{ ready: boolean; acquisition?: unknown }>(
      `${consoleUrl}/api/datasources/${dataSourceName}/readiness?namespace=default&scopeRef=${encodeURIComponent(scopeRef)}`);
    expect(readiness.ready).toBe(true);
    expect(readiness.acquisition).toBeTruthy();
    await this.open(`${consoleUrl}/data-sources/${dataSourceName}?namespace=default&scopeRef=${encodeURIComponent(scopeRef)}&view=acquisitions`,
      TestIds.schemaDrivenSources.dataSourceAcquisitions);
    const parameters = this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitionParameters);
    const start = this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceStartAcquisition);
    await expect(start).toBeDisabled();
    await expect(field(parameters, '$.query').locator('input')).toHaveAttribute('minlength', '3');
    const parametersEditor = parameters.getByTestId(TestIds.common.schemaInputEditor);
    const parametersRaw = await switchToRaw(parametersEditor);
    await parametersRaw.fill(JSON.stringify({ query: 'x', limit: 4 }, null, 2));
    await expect(parametersRaw).toHaveValue(/"query": "x"/);
    await expect(parametersEditor.getByRole('alert').first()).toBeVisible();
    await expect(start).toBeDisabled();
    await parametersRaw.fill(JSON.stringify({ query: 'schema-driven', limit: 4 }, null, 2));
    await expect(start).toBeEnabled();
    await parametersEditor.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(field(parameters, '$.query').locator('input')).toHaveValue('schema-driven');
    await expect(field(parameters, '$.limit').locator('input')).toHaveValue('4');
    await start.click();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.dataSourceAcquisitionRow).first()).toBeVisible();

    let acquisition: AcquisitionWire | undefined;
    await expect.poll(async () => {
      const values = await this.read<AcquisitionWire[]>(`${consoleUrl}/api/datasources/${dataSourceName}/acquisitions?namespace=default&scopeRef=${encodeURIComponent(scopeRef)}`);
      acquisition = values[0];
      return acquisition?.manifest?.artifacts?.length ?? 0;
    }, { timeout: 90_000 }).toBe(1);
    expect(acquisition?.parameters).toEqual({ query: 'schema-driven', limit: 4 });
  }

  private async createKnowledgeSource(consoleUrl: string, scopeRef: string, dataSourceName: string, knowledgeSourceName: string,
    transformationFlow: string, projectionFlow: string): Promise<void> {
    await this.open(`${consoleUrl}/knowledge-sources/new`, TestIds.schemaDrivenSources.knowledgeSourceEditor);
    await selectContaining(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjectionFlow), projectionFlow);
    const binding = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBinding).first();
    await binding.getByRole('textbox').first().fill('origin');
    await selectContaining(binding.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBindingDataSource), dataSourceName);
    await selectContaining(binding.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceBindingTransformationFlow), transformationFlow);
    const configuration = binding.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceTransformationConfiguration);
    const save = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceSave);
    await expect(field(configuration, '$.prefix').locator('input')).toHaveAttribute('minlength', '3');
    const configurationEditor = configuration.getByTestId(TestIds.common.schemaInputEditor);
    const configurationRaw = await switchToRaw(configurationEditor);
    await configurationRaw.fill(JSON.stringify({ prefix: 'x', mode: 'compact' }, null, 2));
    await expect(configurationRaw).toHaveValue(/"prefix": "x"/);
    await expect(configurationEditor.getByRole('alert').first()).toBeVisible();
    await expect(save).toBeDisabled();
    await configurationRaw.fill(JSON.stringify({ prefix: 'docs', mode: 'compact' }, null, 2));
    await expect(save).toBeEnabled();
    await configurationEditor.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(field(configuration, '$.prefix').locator('input')).toHaveValue('docs');
    await expect(field(configuration, '$.mode').locator('select')).toHaveValue('"compact"');
    await expectApi(await this.page.request.post(`${consoleUrl}/api/knowledgesources`, {
      data: {
        name: knowledgeSourceName, namespace: 'default', properties: {
          displayName: knowledgeSourceName, enabled: true,
          dataSources: [{
            name: 'origin', dataSource: { name: dataSourceName, namespace: 'default', scopeRef },
            transformationFlow: { name: transformationFlow, namespace: 'default', version: '1.0.0', useActiveVersion: false },
            configuration: { prefix: 'docs', mode: 'compact' }, required: true,
          }],
          projectionFlow: { name: projectionFlow, namespace: 'default', version: '1.0.0', useActiveVersion: false },
          retrievalFlow: { name: 'knowledge-retrieval-builtin', namespace: 'default', version: '1.0.0', useActiveVersion: false },
        },
      },
    }), 201, `create Knowledge Source '${knowledgeSourceName}'`);
  }

  private async verifyKnowledgeEdit(consoleUrl: string, knowledgeSourceName: string): Promise<void> {
    await this.open(`${consoleUrl}/knowledge-sources/${knowledgeSourceName}/edit?namespace=default`,
      TestIds.schemaDrivenSources.knowledgeSourceEditor);
    const configuration = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceTransformationConfiguration);
    await expect(field(configuration, '$.prefix').locator('input')).toHaveValue('docs');
    await expect(field(configuration, '$.mode').locator('select')).toHaveValue('"compact"');
  }

  private async runProjection(consoleUrl: string, knowledgeSourceName: string): Promise<void> {
    await this.open(`${consoleUrl}/knowledge-sources/${knowledgeSourceName}?namespace=default&view=projections`,
      TestIds.schemaDrivenSources.knowledgeSourceProjectionsTab);
    const parameters = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjectionParameters);
    const start = this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceStartProjection);
    await expect(start).toBeDisabled();
    await expect(field(parameters, '$.audience').locator('input')).toHaveAttribute('minlength', '3');
    const parametersEditor = parameters.getByTestId(TestIds.common.schemaInputEditor);
    const parametersRaw = await switchToRaw(parametersEditor);
    await parametersRaw.fill(JSON.stringify({ audience: 'x', includeMetadata: true }, null, 2));
    await expect(parametersRaw).toHaveValue(/"audience": "x"/);
    await expect(parametersEditor.getByRole('alert').first()).toBeVisible();
    await expect(start).toBeDisabled();
    await parametersRaw.fill(JSON.stringify({ audience: 'operators', includeMetadata: true }, null, 2));
    await expect(start).toBeEnabled();
    await parametersEditor.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(field(parameters, '$.audience').locator('input')).toHaveValue('operators');
    await expect(field(parameters, '$.includeMetadata').locator('input')).toBeChecked();
    await start.click();
    await expect(this.page.getByTestId(TestIds.schemaDrivenSources.knowledgeSourceProjectionRow).first()).toBeVisible();

    let projection: ProjectionWire | undefined;
    await expect.poll(async () => {
      const values = await this.read<ProjectionWire[]>(`${consoleUrl}/api/knowledgesources/${knowledgeSourceName}/projections?namespace=default`);
      projection = values[0];
      return projection?.snapshotName ?? null;
    }, { timeout: 90_000 }).not.toBeNull();
    expect(projection?.parameters).toEqual({ audience: 'operators', includeMetadata: true });
  }

  private async createProfile(consoleUrl: string, scopeRef: string, name: string, displayName: string,
    acquisitionFlow: string, schema: Record<string, unknown>): Promise<void> {
    await expectApi(await this.page.request.post(`${consoleUrl}/api/datasourceprofiles`, {
      data: {
        name, namespace: 'default', scopeRef,
        properties: {
          displayName, enabled: true, version: '1.0.0', configurationSchema: schema,
          acquisitionFlow: { name: acquisitionFlow, version: '1.0.0', useActiveVersion: false },
          toolBindings: [], limits: {}, policies: {},
        },
      },
    }), 201, `create Data Source Profile '${name}'`);
    await expectApi(await this.page.request.post(
      `${consoleUrl}/api/datasourceprofiles/${name}/revisions?namespace=default&scopeRef=${encodeURIComponent(scopeRef)}`,
      { data: { version: '1.0.0', activate: true } }), 201, `publish Data Source Profile '${name}'`);
  }

  private async assertRawRoundTrip(container: Locator, fragments: readonly string[]): Promise<void> {
    const editor = container.getByTestId(TestIds.common.schemaInputEditor);
    const raw = await switchToRaw(editor);
    for (const fragment of fragments) await expect(raw).toHaveValue(new RegExp(escapeRegExp(fragment)));
    await editor.getByTestId(TestIds.common.schemaFormMode).click();
  }

  private async workspaceId(): Promise<string> {
    const workspaceId = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error('The authenticated Console shell does not expose its Workspace identifier.');
    return workspaceId;
  }

  private async scopeRef(): Promise<string> {
    return this.fixtureScopeRef ?? `/workspaces/${await this.workspaceId()}`;
  }

  private async open(url: string, marker: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Schema-driven source route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(marker).waitFor({ state: 'visible' });
  }

  private async read<T>(url: string): Promise<T> {
    const response = await this.page.request.get(url);
    await expectApi(response, 200, `read '${new URL(url).pathname}'`);
    return await response.json() as T;
  }
}

function field(root: Locator, path: string): Locator { return root.locator(`[data-schema-path="${path}"]`); }

async function selectContaining(select: Locator, text: string): Promise<void> {
  const option = select.locator('option').filter({ hasText: text }).first();
  await option.waitFor({ state: 'attached' });
  const value = await option.getAttribute('value');
  if (!value) throw new Error(`No selectable option contains '${text}'.`);
  await select.selectOption(value);
}

async function switchToRaw(editor: Locator): Promise<Locator> {
  const raw = editor.locator('textarea.runner-json');
  await expect(async () => {
    await editor.getByTestId(TestIds.common.schemaRawMode).click();
    await expect(raw).toBeVisible({ timeout: 2_000 });
  }).toPass({ timeout: 15_000 });
  return raw;
}

async function expectApi(response: APIResponse, status: number, operation: string): Promise<void> {
  if (response.status() === status) return;
  throw new Error(`Unable to ${operation}: HTTP ${response.status()} ${await response.text()}`);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
