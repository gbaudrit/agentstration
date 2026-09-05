import { ProductPages } from '../src/pages/product.pages.js';
import { createAgent, type CreateAgentInput } from '../src/journeys/create-agent.journey.js';
import { ignoreCheckpoints } from '../src/journeys/journey.js';
import { expect, test } from '../src/fixtures/test.js';

const agent: CreateAgentInput = {
  name: 'playwright-welcome',
  displayName: 'Playwright welcome agent',
  description: 'Welcomes users during the browser journey.',
  instructions: 'Welcome the user and answer concisely.',
  modelProfile: 'default:reasoning-default',
  runtimeProfile: 'default:maf-builtin',
};

test('an administrator can create and deploy an agent @smoke', async ({ page, product }) => {
  await createAgent({
    ...product,
    pages: new ProductPages(page),
    checkpoint: ignoreCheckpoints,
  }, agent);

  await expect(page).toHaveURL(new RegExp(`/agents/${agent.name}$`));
  await expect(page.getByTestId('agent-name')).toHaveValue(agent.name);
  await expect(page.getByTestId('agent-name')).toBeDisabled();
});
