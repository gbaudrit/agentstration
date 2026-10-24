import { expect, type APIResponse, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

interface ContractFixtures {
  schemaName: string;
  flowName: string;
  agentName: string;
  toolDefinitionName: string;
  sharedConnectionFlow: string;
  inlineConnectionFlow: string;
}

const jsonSchemaDialect = 'https://json-schema.org/draft/2020-12/schema';
const sharedContract = {
  type: 'object',
  properties: { request: { type: 'string', minLength: 1 } },
  required: ['request'],
  additionalProperties: false,
};
const inlineContract = {
  type: 'object',
  properties: { response: { type: 'string' } },
  required: ['response'],
  additionalProperties: false,
};

export class SchemaContractsPage {
  public constructor(private readonly page: Page) {}

  public async createFixtures(consoleUrl: string): Promise<ContractFixtures> {
    const suffix = Date.now().toString();
    const workspaceId = await this.page.getByTestId(TestIds.console.shell).getAttribute('data-workspace-id');
    if (!workspaceId) throw new Error('The Console shell did not expose the active Workspace id.');
    const scopeRef = `/workspaces/${workspaceId}`;
    // Resource pickers intentionally page a sorted catalog; keep deterministic fixtures at its start.
    const schemaName = `aaa-browser-contract-${suffix}`;
    const flowName = `aaa-browser-contract-flow-${suffix}`;
    const agentName = `aaa-browser-contract-agent-${suffix}`;
    const toolDefinitionName = `aaa-browser-contract-tool-${suffix}`;
    const inlineToolFlowName = `aaa-browser-inline-tool-flow-${suffix}`;

    await expectApi(await this.page.request.post(`${consoleUrl}/api/schemas`, {
      data: {
        name: schemaName,
        namespace: 'default',
        scopeRef,
        properties: {
          displayName: 'Browser reusable contract',
          description: 'Deterministic schema contract fixture.',
          format: 'json-schema',
          dialect: jsonSchemaDialect,
          content: sharedContract,
        },
      },
    }), 201, 'create reusable Schema');
    const schemaQuery = `scopeRef=${encodeURIComponent(scopeRef)}&resourceNamespace=default`;
    await expectApi(await this.page.request.post(
      `${consoleUrl}/api/schemas/${encodeURIComponent(schemaName)}/versions?${schemaQuery}`,
      { data: { version: '1.0.0' } },
    ), 201, 'publish reusable Schema');

    const sharedBinding = { reference: { name: schemaName, namespace: 'default', scopeRef, version: '1.0.0' } };
    const inlineBinding = { inline: { format: 'json-schema', dialect: jsonSchemaDialect, content: inlineContract } };
    await this.createAgent(consoleUrl, agentName, sharedBinding, inlineBinding);
    await this.createPublishedFlow(consoleUrl, flowName, sharedBinding, inlineBinding);
    await this.createPublishedFlow(consoleUrl, inlineToolFlowName, inlineBinding, inlineBinding);
    await this.createToolDefinition(consoleUrl, toolDefinitionName, inlineToolFlowName, inlineBinding);

    const sharedConnectionFlow = await this.createConnectionFlow(consoleUrl, `aaa-browser-agent-link-${suffix}`, {
      type: 'agent', name: 'target', displayName: 'Contract Agent',
      agent: { resourceId: agentName }, inputMapping: '${transition.output}',
    });
    const inlineConnectionFlow = await this.createConnectionFlow(consoleUrl, `aaa-browser-tool-link-${suffix}`, {
      type: 'tool', name: 'target', displayName: 'Contract Tool',
      tool: { resourceId: `agentstration.${toolDefinitionName}` }, argumentsMapping: '${transition.output}',
    });
    return { schemaName, flowName, agentName, toolDefinitionName, sharedConnectionFlow, inlineConnectionFlow };
  }

  public async exerciseFlowBindings(consoleUrl: string, fixtures: ContractFixtures): Promise<void> {
    await this.openFlow(consoleUrl, fixtures.flowName);
    await this.openStep('Input');
    await this.page.getByTestId('step-settings-tab-input').click();
    const input = this.page.getByTestId('flow-input-schema');
    await expect(input).toBeVisible();
    await expect(input.getByTestId('flow-input-schema-mode-shared')).toHaveClass(/active/);
    await expect(input.getByTestId('flow-input-schema-resource')).toContainText('Browser reusable contract');
    await expect(input.getByTestId('flow-input-schema-version')).toHaveValue('1.0.0');
    await this.closeStep();

    await this.openStep('Completed');
    await this.page.getByTestId('step-settings-tab-output').click();
    await expect(this.page.getByTestId('terminal-output-contract')).toBeVisible();
    await expect(this.page.getByTestId(TestIds.flowObservability.outputMapping)).toContainText('Incoming transition output');
    await this.closeStep();
  }

  public async exerciseAgentBindings(consoleUrl: string, fixtures: ContractFixtures): Promise<void> {
    await this.open(`${consoleUrl}/agents/${encodeURIComponent(fixtures.agentName)}?namespace=default`, TestIds.agentEditor.form);
    await this.page.locator(`[data-testid="${TestIds.agentEditor.form}"][data-interactive="true"]`).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.schemaContracts.agentTab).click();
    await expect(this.page.getByTestId(TestIds.schemaContracts.agentSection)).toBeVisible();
    const input = this.page.getByTestId(TestIds.schemaContracts.agentInput);
    await expect(input.getByTestId(`${TestIds.schemaContracts.agentInput}-mode-shared`)).toHaveClass(/active/);
    await expect(input.getByTestId(`${TestIds.schemaContracts.agentInput}-version`)).toHaveValue('1.0.0');
    await this.page.getByTestId(TestIds.schemaContracts.agentOutputTab).click();
    const output = this.page.getByTestId(TestIds.schemaContracts.agentOutput);
    await expect(output.getByTestId(`${TestIds.schemaContracts.agentOutput}-mode-inline`)).toHaveClass(/active/);
    await expect(output.locator('.schema-property-tree')).toContainText('response');
  }

  public async exerciseToolFlowContracts(consoleUrl: string, fixtures: ContractFixtures): Promise<void> {
    await this.open(`${consoleUrl}/tools/definitions/new`, TestIds.resourceAdministration.toolDefinitionEditor);
    const flow = this.page.getByTestId(TestIds.schemaContracts.toolFlow);
    const flowValue = await flow.locator('option').filter({ hasText: `Contract flow ${fixtures.flowName}` }).getAttribute('value');
    if (!flowValue) throw new Error(`The ToolDefinition editor did not list Flow '${fixtures.flowName}'.`);
    await expect(async () => {
      await flow.selectOption(flowValue);
      await expect(this.page.getByTestId(TestIds.schemaContracts.toolInputTab)).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
    const input = this.page.getByTestId(TestIds.schemaContracts.toolInput);
    await expect(input).toBeVisible();
    await expect(input.locator('input, select, textarea').first()).toBeDisabled();
    await this.page.getByTestId(TestIds.schemaContracts.toolOutputTab).click();
    const output = this.page.getByTestId(TestIds.schemaContracts.toolOutput);
    await expect(output).toBeVisible();
    await expect(output.locator('.schema-property-tree')).toContainText('response');
    await expect(output.locator('input, select, textarea').first()).toBeDisabled();
  }

  public async exerciseConnectionPrompts(consoleUrl: string, fixtures: ContractFixtures): Promise<void> {
    await this.openFlow(consoleUrl, fixtures.sharedConnectionFlow);
    await this.fitCanvas();
    await this.closeCompactInspectorIfVisible();
    await this.dragOutputToInput('Input', 'Contract Agent');
    const dialog = this.page.getByTestId(TestIds.schemaContracts.connectionDialog);
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Contract Agent');
    await expect.poll(() => dialog.evaluate(element => {
      const backdrop = element.closest('.connection-contract-backdrop');
      return backdrop ? getComputedStyle(backdrop).zIndex : 'missing';
    })).toBe('140');
    await this.page.getByTestId(TestIds.schemaContracts.connectionApply).click();
    await expect(dialog).toBeHidden();
    await this.assertInputBinding('shared', '1.0.0');
    await this.saveAndReload(consoleUrl, fixtures.sharedConnectionFlow);
    await this.assertInputBinding('shared', '1.0.0');

    await this.openFlow(consoleUrl, fixtures.inlineConnectionFlow);
    await this.fitCanvas();
    await this.closeCompactInspectorIfVisible();
    await this.dragOutputToInput('Input', 'Contract Tool');
    await expect(dialog).toBeVisible();
    await expect(dialog).toContainText('Contract Tool');
    await this.page.getByTestId(TestIds.schemaContracts.connectionLater).click();
    await expect(dialog).toBeHidden();
    await this.assertInputBinding('empty');
  }

  public async exerciseResponsiveConnectionPrompt(consoleUrl: string, fixtures: ContractFixtures): Promise<void> {
    await this.page.setViewportSize({ width: 1440, height: 1000 });
    await this.openFlow(consoleUrl, fixtures.sharedConnectionFlow);
    await this.fitCanvas();
    await this.closeCompactInspectorIfVisible();
    await this.dragOutputToInput('Input', 'Contract Agent');
    const dialog = this.page.getByTestId(TestIds.schemaContracts.connectionDialog);
    await expect(dialog).toBeVisible();

    await this.page.setViewportSize({ width: 390, height: 844 });
    await expect(dialog).toBeVisible();
    const bounds = await dialog.boundingBox();
    expect(bounds).not.toBeNull();
    expect(bounds!.x).toBeGreaterThanOrEqual(0);
    expect(bounds!.x + bounds!.width).toBeLessThanOrEqual(390);
    await expect(this.page.getByTestId(TestIds.schemaContracts.connectionApply)).toBeVisible();
    await this.page.getByTestId(TestIds.schemaContracts.connectionLater).click();
    await expect(dialog).toBeHidden();
  }

  private async assertInputBinding(mode: 'shared' | 'empty', version?: string): Promise<void> {
    await this.openStep('Input');
    await this.page.getByTestId('step-settings-tab-input').click();
    const input = this.page.getByTestId('flow-input-schema');
    if (mode === 'empty') await expect(input.getByTestId('flow-input-schema-empty')).toBeVisible();
    else {
      await expect(input.getByTestId('flow-input-schema-mode-shared')).toHaveClass(/active/);
      await expect(input.getByTestId('flow-input-schema-version')).toHaveValue(version ?? '');
    }
    await this.closeStep();
  }

  private async createAgent(consoleUrl: string, name: string, inputBinding: object, outputBinding: object): Promise<void> {
    await expectApi(await this.page.request.put(`${consoleUrl}/api/agents/${encodeURIComponent(name)}`, {
      headers: { 'If-None-Match': '*' },
      data: {
        apiVersion: 'agentstration.io/v1', kind: 'Agent', metadata: { name, namespace: 'default' },
        definition: {
          displayName: 'Contract Agent', description: 'Schema contract fixture.', instructions: 'Return a deterministic result.',
          modelProfile: { name: 'reasoning-default', namespace: 'default' },
          runtimeProfile: { name: 'maf-builtin', namespace: 'default' },
          tools: [], toolSets: [], behaviors: [], middleware: [], contextProviders: [], settings: {},
          inputSchemaBinding: inputBinding, outputSchemaBinding: outputBinding,
        },
      },
    }), 200, 'create contract Agent');
  }

  private async createPublishedFlow(consoleUrl: string, name: string, inputBinding: object, outputBinding: object): Promise<void> {
    const created = await this.createDraft(consoleUrl, name, `Contract flow ${name}`);
    const definition = {
      entryStep: 'input', inputSchemaBinding: inputBinding, outputSchemaBinding: outputBinding,
      steps: [
        { type: 'input', name: 'input', displayName: 'Input', schemaBinding: inputBinding },
        { type: 'output', name: 'completed', displayName: 'Completed', outcome: 'success', outputMapping: '${transition.output}', schemaBinding: outputBinding },
      ],
      transitions: [{ id: 'input-completed', fromStep: 'input', event: 'completed', toStep: 'completed' }],
      designer: { preferredLayout: 'Horizontal', nodePositions: { input: { x: 80, y: 220 }, completed: { x: 520, y: 220 } } },
    };
    await this.updateDraft(consoleUrl, name, created, definition);
    await expectApi(await this.page.request.post(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/publish`, {
      data: { version: '1.0.0', activate: true },
    }), 201, 'publish contract Flow');
  }

  private async createToolDefinition(consoleUrl: string, name: string, flowName: string, inputBinding: object): Promise<void> {
    await expectApi(await this.page.request.post(`${consoleUrl}/api/tooldefinitions`, {
      data: {
        name,
        namespace: 'default',
        properties: {
          displayName: 'Contract Tool', description: 'Schema contract fixture.', enabled: true,
          requiresApproval: false, invocationTimeoutSeconds: 90,
          inputSchema: inlineContract, inputSchemaBinding: inputBinding,
          outputSchema: inlineContract,
          flow: { name: flowName, namespace: 'default', useActiveVersion: true },
        },
      },
    }), 201, 'create contract ToolDefinition');
  }

  private async createConnectionFlow(consoleUrl: string, name: string, target: Record<string, unknown>): Promise<string> {
    const created = await this.createDraft(consoleUrl, name, `Connection contract ${name}`);
    await this.updateDraft(consoleUrl, name, created, {
      entryStep: 'input',
      steps: [{ type: 'input', name: 'input', displayName: 'Input' }, target], transitions: [],
      designer: { preferredLayout: 'Horizontal', nodePositions: { input: { x: 80, y: 220 }, target: { x: 520, y: 220 } } },
    });
    return name;
  }

  private async createDraft(consoleUrl: string, name: string, displayName: string): Promise<{ value: Record<string, unknown>; eTag: string }> {
    const response = await this.page.request.post(`${consoleUrl}/api/flows/drafts`, {
      data: { name, displayName, template: 'Empty' },
    });
    await expectApi(response, 201, 'create Flow draft');
    return await response.json() as { value: Record<string, unknown>; eTag: string };
  }

  private async updateDraft(consoleUrl: string, name: string, draft: { value: Record<string, unknown>; eTag: string }, definition: object): Promise<void> {
    await expectApi(await this.page.request.put(`${consoleUrl}/api/flows/${encodeURIComponent(name)}/draft`, {
      headers: { 'If-Match': draft.eTag },
      data: { displayName: draft.value.displayName, description: draft.value.description, tags: draft.value.tags, definition },
    }), 200, 'update Flow draft');
  }

  private async openFlow(consoleUrl: string, name: string): Promise<void> {
    await this.open(`${consoleUrl}/namespaces/default/flows/${encodeURIComponent(name)}/designer`, TestIds.flowObservability.designer);
  }

  private async closeCompactInspectorIfVisible(): Promise<void> {
    const close = this.page.getByRole('button', { name: 'Close inspector' });
    if (await close.isVisible()) {
      await close.click();
      await expect(close).toBeHidden();
    }
  }

  private async fitCanvas(): Promise<void> {
    const canvas = this.page.locator('.flow-diagram-canvas');
    await expect(async () => {
      await canvas.scrollIntoViewIfNeeded();
      await this.page.getByRole('button', { name: 'Fit', exact: true }).click();
      await expect(canvas).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  private async openStep(displayName: string): Promise<void> {
    await expect(async () => {
      await this.node(displayName).locator('.flow-node-settings').click();
      await expect(this.page.locator('.step-settings-dialog')).toBeVisible({ timeout: 2_000 });
    }).toPass({ timeout: 20_000 });
  }

  private async closeStep(): Promise<void> {
    await this.page.locator('.step-settings-close').click();
    await expect(this.page.locator('.step-settings-dialog')).toBeHidden();
  }

  private async saveAndReload(consoleUrl: string, name: string): Promise<void> {
    await this.page.getByTestId(TestIds.flowObservability.designerSave).click();
    await expect(this.page.locator('.save-state.saved')).toBeVisible();
    await this.openFlow(consoleUrl, name);
  }

  private async dragOutputToInput(sourceName: string, targetName: string): Promise<void> {
    const source = this.node(sourceName).locator('.flow-port-handle.output .diagram-port').first();
    const target = this.node(targetName).locator('.flow-port-handle.input .diagram-port');
    await expect(async () => {
      await expect(source).toBeVisible({ timeout: 2_000 });
      await expect(target).toBeVisible({ timeout: 2_000 });
      const sourceBox = await source.boundingBox();
      const targetBox = await target.boundingBox();
      expect(sourceBox, `Output endpoint for '${sourceName}'`).not.toBeNull();
      expect(targetBox, `Input endpoint for '${targetName}'`).not.toBeNull();
      await this.page.mouse.move(sourceBox!.x + sourceBox!.width / 2, sourceBox!.y + sourceBox!.height / 2);
      await this.page.mouse.down();
      await this.page.mouse.move(targetBox!.x + targetBox!.width / 2, targetBox!.y + targetBox!.height / 2, { steps: 10 });
      await this.page.mouse.up();
    }).toPass({ timeout: 20_000 });
  }

  private node(name: string): Locator {
    return this.page.locator('.flow-node:visible').filter({
      has: this.page.locator('strong').filter({ hasText: new RegExp(`^${escapeRegExp(name)}$`, 'i') }),
    }).first();
  }

  private async open(url: string, testId: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Route '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.getByTestId(testId).waitFor({ state: 'visible' });
  }
}

async function expectApi(response: APIResponse, status: number, operation: string): Promise<void> {
  expect(response.status(), `${operation}: ${await response.text()}`).toBe(status);
}

function escapeRegExp(value: string): string {
  return value.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
}
