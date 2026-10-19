import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectPlatformHealthInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const inspectPlatformHealth: Journey<InspectPlatformHealthInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const health = context.pages.platformHealth;

  await health.open(context.consoleUrl, '/agents');
  await health.expectOperational();
  await context.checkpoint({ name: Checkpoints.platformHealth.directRoute, page: context.pages.page, target: health.indicator });

  await health.open(context.consoleUrl, '/');
  await health.navigateToAgents();
  await health.expectOperational();
  await context.checkpoint({ name: Checkpoints.platformHealth.afterNavigation, page: context.pages.page, target: health.indicator });
};
