import { Checkpoints } from '../contracts/checkpoints.js';
import type { ModelAdministrationDefinition } from '../pages/model-administration.page.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export type ExerciseModelAdministrationInput = ModelAdministrationDefinition & {
  username?: string;
  password?: string;
  workspaceName?: string;
};

export const exerciseModelAdministration: Journey<ExerciseModelAdministrationInput> = async (context, input) => {
  validate(input);
  await prepareConsoleJourney(context, input);
  const administration = context.pages.modelAdministration;

  const provider = await administration.createProvider(context.consoleUrl, input);
  await context.checkpoint({ name: Checkpoints.modelAdministration.provider, page: context.pages.page, target: provider });
  await administration.updateProviderDisplayName(`${input.providerDisplayName} updated`);

  const runtime = await administration.createRuntime(context.consoleUrl, input);
  await context.checkpoint({ name: Checkpoints.modelAdministration.runtime, page: context.pages.page, target: runtime });
  await administration.updateRuntimeDisplayName(`${input.runtimeDisplayName} updated`);

  const profile = await administration.createProfile(context.consoleUrl, input);
  await context.checkpoint({ name: Checkpoints.modelAdministration.profile, page: context.pages.page, target: profile });
  await administration.updateProfileDisplayName(`${input.profileDisplayName} updated`);
  await context.checkpoint({ name: Checkpoints.modelAdministration.updated, page: context.pages.page, target: profile });

  await administration.deleteProfile();
  await administration.openRuntime(context.consoleUrl, input.runtimeName);
  await administration.deleteRuntime();
  await administration.openProvider(context.consoleUrl, input.providerName);
  await administration.deleteProvider();
  await context.checkpoint({ name: Checkpoints.modelAdministration.deleted, page: context.pages.page });
};

function validate(input: ExerciseModelAdministrationInput): void {
  for (const key of ['providerName', 'providerDisplayName', 'profileName', 'profileDisplayName', 'runtimeName', 'runtimeDisplayName'] as const) {
    if (!input[key]?.trim()) throw new Error(`Model administration input '${key}' is required.`);
  }
}
