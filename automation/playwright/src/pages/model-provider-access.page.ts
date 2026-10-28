import { expect, type Locator, type Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';
import { fillAndCommit } from './controls.js';

export class ModelProviderAccessPage {
  public constructor(private readonly page: Page) {}

  public get runDetails(): Locator {
    return this.page.getByTestId(TestIds.flowObservability.agentRunDetails);
  }

  public async expectProviderHidden(consoleUrl: string, providerName: string): Promise<void> {
    const response = await this.page.request.get(
      `${consoleUrl}/api/modelproviders/${encodeURIComponent(providerName)}?scopeRef=${encodeURIComponent('/instance')}`,
    );
    const body = await response.text();
    expect(response.status(), body).toBe(404);
  }

  public async runAgent(consoleUrl: string, agentName: string, prompt: string): Promise<string> {
    const response = await this.page.goto(
      `${consoleUrl}/agents/${encodeURIComponent(agentName)}/run`,
      { waitUntil: 'domcontentloaded' },
    );
    if (!response?.ok()) throw new Error(`Agent runner returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.page.locator(
      `[data-testid="${TestIds.flowObservability.agentRunner}"][data-interactive="true"]`,
    ).waitFor({ state: 'visible' });
    const input = this.page.getByTestId(TestIds.flowObservability.agentRunPrompt);
    await fillAndCommit(input, prompt);
    await this.page.getByTestId(TestIds.flowObservability.agentRunSubmit).click();
    await this.runDetails.waitFor({ state: 'visible' });
    await expect.poll(() => this.runDetails.getAttribute('data-run-state'), { timeout: 90_000 }).toBe('Succeeded');
    const runId = await this.runDetails.getAttribute('data-run-id');
    if (!runId) throw new Error(`Agent '${agentName}' did not expose its Runtime Run identifier.`);
    return runId;
  }

  public async expectAgentRunRejected(consoleUrl: string, agentName: string): Promise<void> {
    const agent = await this.page.request.get(`${consoleUrl}/api/agents/${encodeURIComponent(agentName)}`);
    const agentBody = await agent.text();
    expect(agent.status(), agentBody).toBe(200);
    const resource = JSON.parse(agentBody) as { uid: string; generation: number; scopeRef: string; namespace: string };
    const response = await this.page.request.post(`${consoleUrl}/api/runtime/runs`, {
      data: {
        executionScopeRef: resource.scopeRef,
        agent: {
          resourceId: agentName,
          version: resource.generation,
          namespace: resource.namespace,
          scopeRef: resource.scopeRef,
          resourceUid: resource.uid,
        },
        input: { messages: [{ role: 'User', content: 'This execution must be rejected after Provider revocation.' }] },
        origin: 'Api',
      },
    });
    const body = await response.text();
    expect(response.status(), body).toBe(202);
    let run = JSON.parse(body) as RuntimeRunWire;
    await expect.poll(async () => {
      const current = await this.page.request.get(
        `${consoleUrl}/api/runtime/runs/${encodeURIComponent(run.id)}?scopeRef=${encodeURIComponent(resource.scopeRef)}`,
      );
      const currentBody = await current.text();
      expect(current.status(), currentBody).toBe(200);
      run = JSON.parse(currentBody) as RuntimeRunWire;
      return run.status.state;
    }, { timeout: 90_000 }).toBe('Failed');
    expect(JSON.stringify(run.status)).not.toContain('ollama');
  }
}

interface RuntimeRunWire {
  id: string;
  status: {
    state: string;
    error?: string | null;
    errorCode?: string | null;
  };
}
