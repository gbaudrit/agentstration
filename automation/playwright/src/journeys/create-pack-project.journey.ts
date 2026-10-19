import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { PackProjectDefinition } from '../pages/distribution.page.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export type CreatePackProjectInput = PackProjectDefinition & { username?: string; password?: string };

export const createPackProject: Journey<CreatePackProjectInput> = async (context, input) => {
  validate(input);
  await prepareConsoleJourney(context, input);
  await context.pages.distribution.open(context.consoleUrl, '/pack-projects/new', 'packComposer');
  await context.checkpoint({ name: Checkpoints.createPackProject.composer, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.distribution.packComposer) });
  await context.pages.distribution.createPackProject(context.consoleUrl, input);
  await context.checkpoint({ name: Checkpoints.createPackProject.created, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.distribution.packProjectDetails) });
  await context.pages.distribution.buildPackProject();
  await context.checkpoint({ name: Checkpoints.createPackProject.built, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.distribution.packProjectStatus) });
};

function validate(input: CreatePackProjectInput): void {
  for (const key of ['publisher', 'name', 'version', 'displayName'] as const) {
    if (!input[key]?.trim()) throw new Error(`Create Pack Project input '${key}' is required.`);
  }
}
