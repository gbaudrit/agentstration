import { expect, type APIResponse, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import type { ExpectedText } from '../locales/expected-text.js';

export interface ExecutionResourceCounts {
  workItems: number;
  flowRuns: number;
  runtimeRuns: number;
  notifications: number;
}

export interface ToolRunOutput {
  dryRun: boolean;
  title?: string;
  message?: string;
  notificationId?: string;
}

export class ToolExecutionPage {
  public constructor(private readonly page: Page) {}

  public get details(): Locator { return this.page.getByTestId(TestIds.resourceAdministration.toolDetails); }
  public get runner(): Locator { return this.page.getByTestId(TestIds.toolExecution.runner); }
  public get result(): Locator { return this.page.getByTestId(TestIds.toolExecution.result); }

  public async open(consoleUrl: string, name: string, namespace?: string): Promise<void> {
    const query = namespace ? `?namespace=${encodeURIComponent(namespace)}` : '';
    const resource = await this.page.request.get(`${consoleUrl}/api/tools/${encodeURIComponent(name)}${query}`);
    await expectApi(resource, 200, `read Tool '${name}'`);
    const body = await resource.json() as { definition?: { displayName?: string } };
    const displayName = body.definition?.displayName;
    if (!displayName) throw new Error(`Tool '${name}' did not expose a display name.`);
    const response = await this.page.goto(`${consoleUrl}/tools/${encodeURIComponent(name)}${query}`, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Tool details returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.details.waitFor({ state: 'visible' });
    await expect(this.page).toHaveTitle(`${displayName} · Agentstration`);
  }

  public async openExecution(): Promise<void> {
    await expect(async () => {
      await this.page.getByTestId(TestIds.toolExecution.executionTab).click();
      await expect(this.runner).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  public async assertLocalized(expected: ExpectedText['toolExecution']): Promise<void> {
    await expect(this.page.getByTestId(TestIds.toolExecution.overviewTab)).toHaveText(expected.overview);
    await expect(this.page.getByTestId(TestIds.toolExecution.executionTab)).toHaveText(expected.execution);
    await this.openExecution();
    await expect(this.runner.getByRole('heading', { name: expected.runTool })).toBeVisible();
    await expect(this.runner.getByRole('heading', { name: expected.input })).toBeVisible();
    await expect(this.page.getByTestId(TestIds.toolExecution.simulateMode)).toContainText(expected.simulation);
    await expect(this.page.getByTestId(TestIds.toolExecution.executeMode)).toContainText(expected.realExecution);
  }

  public async assertSimulationReady(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.toolExecution.simulateMode)).toHaveClass(/\bselected\b/);
    await expect(this.page.getByTestId(TestIds.toolExecution.dryRunManaged)).toBeVisible();
    const dryRun = this.field('dryRun').locator('input');
    await expect(dryRun).toBeChecked();
    await expect(dryRun).toBeDisabled();
    await expect(this.page.getByTestId(TestIds.toolExecution.submit)).toBeDisabled();
  }

  public async assertRawCannotOverrideDryRun(): Promise<void> {
    await this.page.getByTestId(TestIds.common.schemaRawMode).click();
    const raw = this.rawInput();
    await expect(raw).not.toHaveValue(/dryRun/);
    await raw.fill('{"title":"Raw preview","message":"Raw input","dryRun":false}');
    await expect(raw).not.toHaveValue(/dryRun/);
    await this.page.getByTestId(TestIds.common.schemaFormMode).click();
    await expect(this.field('dryRun').locator('input')).toBeChecked();
  }

  public async fillNotification(title: string, message: string): Promise<void> {
    await this.fillField('title', title);
    await this.fillField('message', message);
    await expect(this.page.getByTestId(TestIds.toolExecution.submit)).toBeEnabled();
  }

  public async run(): Promise<ToolRunOutput> {
    await this.page.getByTestId(TestIds.toolExecution.submit).click();
    await this.result.waitFor({ state: 'visible' });
    const text = await this.page.getByTestId(TestIds.toolExecution.output).locator('pre').textContent();
    if (!text) throw new Error('The Tool run did not expose structured output.');
    return JSON.parse(text) as ToolRunOutput;
  }

  public async selectRealExecution(): Promise<void> {
    await this.page.getByTestId(TestIds.toolExecution.executeMode).click();
    await expect(this.page.getByTestId(TestIds.toolExecution.executeMode)).toHaveClass(/\bselected\b/);
    const dryRun = this.field('dryRun').locator('input');
    await expect(dryRun).not.toBeChecked();
    await expect(dryRun).toBeEnabled();
  }

  public async assertSimulationUnavailable(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.toolExecution.simulateMode)).toBeDisabled();
    await expect(this.page.getByTestId(TestIds.toolExecution.executeMode)).toHaveClass(/\bselected\b/);
    await expect(this.runner.locator('.tool-run-mode-unavailable')).toBeVisible();
  }

  public async assertApprovalRequired(): Promise<void> {
    await expect(this.page.getByTestId(TestIds.toolExecution.simulateMode)).toBeDisabled();
    await expect(this.page.getByTestId(TestIds.toolExecution.executeMode)).toHaveClass(/\bselected\b/);
    await expect(this.runner.locator('.inline-warning')).toBeVisible();
    await expect(this.page.getByTestId(TestIds.toolExecution.submit)).toBeDisabled();
  }

  public async snapshotExecutionResources(consoleUrl: string, workspaceName = 'default'): Promise<ExecutionResourceCounts> {
    const [workItems, flowRuns, runtimeRuns, notifications] = await Promise.all([
      this.countValues(`${consoleUrl}/api/work/workitems?top=200`),
      this.countValues(`${consoleUrl}/api/flowRuns?top=200`),
      this.countValues(`${consoleUrl}/api/runtime/runs?top=1000`),
      this.countValues(`${consoleUrl}/api/workspaces/${encodeURIComponent(workspaceName)}/notifications`),
    ]);
    return { workItems, flowRuns, runtimeRuns, notifications };
  }

  public async createNonSimulableTools(consoleUrl: string): Promise<{ executable: string; approvalRequired: string }> {
    const suffix = Date.now().toString();
    const flowName = `playwright-tool-run-${suffix}`;
    const created = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name: flowName, displayName: 'Playwright Tool Run', template: 'Empty' },
    });
    await expectApi(created, 201, 'create Flow draft');
    const etag = created.headers()['etag'];
    if (!etag) throw new Error('The Flow draft response did not expose an ETag.');
    const source = `entryStep: input
inputSchema:
  type: object
  properties:
    message:
      type: string
      minLength: 1
  required:
  - message
  additionalProperties: false
outputSchema:
  type: object
  properties:
    message:
      type: string
  required:
  - message
steps:
- type: input
  name: input
  displayName: Input
  schema:
    type: object
    properties:
      message:
        type: string
        minLength: 1
    required:
    - message
    additionalProperties: false
- type: output
  name: output
  displayName: Output
  outputMapping: \"\${input}\"
transitions:
- id: input-output
  fromStep: input
  event: completed
  toStep: output
`;
    const replaced = await this.page.request.put(`${consoleUrl}/api/flows/${flowName}/draft/source`, {
      headers: { 'If-Match': etag },
      data: { source, format: 'yaml', updatedBy: 'playwright' },
    });
    await expectApi(replaced, 200, 'replace Flow source');
    const validation = await this.page.request.post(`${consoleUrl}/api/flows/${flowName}/validate`);
    await expectApi(validation, 200, 'validate Flow draft');
    const validationBody = await validation.json() as { isValid: boolean; issues?: unknown[] };
    if (!validationBody.isValid) throw new Error(`The Tool fixture Flow is invalid: ${JSON.stringify(validationBody.issues)}`);
    const published = await this.page.request.post(`${consoleUrl}/api/flows/${flowName}/publish`, {
      data: { version: '1.0.0', releaseNotes: 'Playwright Tool execution fixture', activate: true },
    });
    await expectApi(published, 201, 'publish Flow');

    const inputSchema = {
      type: 'object', properties: { message: { type: 'string', minLength: 1 } },
      required: ['message'], additionalProperties: false,
    };
    const outputSchema = {
      type: 'object', properties: { message: { type: 'string' } },
      required: ['message'],
    };
    const properties = (displayName: string, requiresApproval: boolean) => ({
      displayName, enabled: true, requiresApproval, inputSchema, outputSchema,
      flow: { name: flowName, useActiveVersion: true }, invocationTimeoutSeconds: 90,
    });
    const executableDefinition = `playwright.execute.${suffix}`;
    const approvalDefinition = `playwright.approval.${suffix}`;
    await expectApi(await this.page.request.post(`${consoleUrl}/api/tooldefinitions`, {
      data: { name: executableDefinition, properties: properties('Playwright real execution', false) },
    }), 201, 'create non-simulable ToolDefinition');
    await expectApi(await this.page.request.post(`${consoleUrl}/api/tooldefinitions`, {
      data: { name: approvalDefinition, properties: properties('Playwright approval required', true) },
    }), 201, 'create approval ToolDefinition');
    return {
      executable: `agentstration.${executableDefinition}`,
      approvalRequired: `agentstration.${approvalDefinition}`,
    };
  }

  private field(name: string): Locator {
    return this.page.locator(`[data-schema-path="$.${name}"]`);
  }

  private rawInput(): Locator {
    return this.page.getByTestId(TestIds.common.schemaInputEditor).locator('textarea.runner-json');
  }

  private async fillField(name: string, value: string): Promise<void> {
    const field = this.field(name).locator('input, textarea');
    await field.fill(value);
    await expect(field).toHaveValue(value);
  }

  private async countValues(url: string): Promise<number> {
    const response = await this.page.request.get(url);
    await expectApi(response, 200, `read ${new URL(url).pathname}`);
    const body = await response.json() as { value?: unknown[] };
    if (!Array.isArray(body.value)) throw new Error(`Endpoint '${url}' did not return a value collection.`);
    return body.value.length;
  }
}

async function expectApi(response: APIResponse, status: number, operation: string): Promise<void> {
  if (response.status() === status) return;
  throw new Error(`Unable to ${operation}: HTTP ${response.status()} ${await response.text()}`);
}
