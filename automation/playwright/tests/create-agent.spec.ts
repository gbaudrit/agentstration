import { ProductPages } from '../src/pages/product.pages.js';
import { TestIds } from '../src/contracts/test-ids.js';
import { createAgent, type CreateAgentInput } from '../src/journeys/create-agent.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { expect, test } from '../src/fixtures/test.js';

const agent: CreateAgentInput = {
  name: 'playwright-welcome',
  displayName: 'Playwright welcome agent',
  description: 'Welcomes users during the browser journey.',
  instructions: 'Welcome the user and answer concisely.',
  modelProfile: ['/instance|default|missing-profile', '/instance|default|reasoning-default'],
  runtimeProfile: '/instance|default|maf-builtin',
};

test('an administrator can create and deploy an agent @smoke', async ({ page, product }) => {
  await createAgent({
    ...product,
    pages: new ProductPages(page),
    checkpoint: ignoreCheckpoints,
  }, agent);

  await expect(page).toHaveURL(url =>
    url.pathname === `/agents/${agent.name}` && url.searchParams.has('scopeRef'));
  await expect(page.getByTestId(TestIds.agentEditor.name)).toHaveValue(agent.name);
  await expect(page.getByTestId(TestIds.agentEditor.name)).toBeDisabled();
});
