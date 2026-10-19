import { Checkpoints } from '../contracts/checkpoints.js';
import { ResourceCreationRoutes, ResourceListRoutes } from '../pages/resource-administration.page.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface InspectResourceAdministrationInput {
  username?: string;
  password?: string;
  workspaceName?: string;
}

export const inspectResourceAdministration: Journey<InspectResourceAdministrationInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const administration = context.pages.resourceAdministration;

  const lists = await administration.openAll(context.consoleUrl, ResourceListRoutes);
  await context.checkpoint({ name: Checkpoints.resourceAdministration.lists, page: context.pages.page, target: lists });

  const profileEditors = await administration.openAll(context.consoleUrl, ResourceCreationRoutes.slice(0, 3));
  await context.checkpoint({ name: Checkpoints.resourceAdministration.profileEditors, page: context.pages.page, target: profileEditors });

  const protectedEditors = await administration.openAll(context.consoleUrl, ResourceCreationRoutes.slice(3));
  await context.checkpoint({ name: Checkpoints.resourceAdministration.protectedEditors, page: context.pages.page, target: protectedEditors });
};
