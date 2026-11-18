import { randomUUID } from 'node:crypto';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { FormAppearance } from '../contracts/shared-form-cases.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { FormAdaptersPage } from '../pages/form-adapters.page.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseFormAdapterInput {
  kind: 'contextual' | 'registry' | 'pack' | 'preferences' | 'administration';
  locale: SupportedTestLocale;
  theme: FormAppearance;
  username?: string;
  password?: string;
}

export const exerciseFormAdapter: Journey<ExerciseFormAdapterInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  await context.pages.sharedForms.configure(context.consoleUrl, input.locale, input.theme);
  const forms = new FormAdaptersPage(context.pages.page);
  switch (input.kind) {
    case 'contextual':
      await context.pages.organizationWorkspaces.selectInstance();
      await context.pages.foundrySecretBinding.createProvider(context.consoleUrl, `forms-context-${randomUUID().slice(0, 8)}`, 'Forms contextual fixture');
      await forms.exerciseContextualValues(context.consoleUrl, input.locale);
      break;
    case 'registry': await forms.exerciseRegistryRequiredSelection(context.consoleUrl, input.locale); break;
    case 'pack':
      await context.pages.organizationWorkspaces.selectByName('default');
      await forms.exercisePackClassifications(context.consoleUrl, input.locale);
      break;
    case 'preferences': await forms.assertLanguageChoiceIsFixed(context.consoleUrl, input.locale); break;
    case 'administration': await forms.exerciseAccountAdministration(context.consoleUrl, input.locale); break;
  }
  await context.checkpoint({ name: input.kind === 'contextual' ? Checkpoints.sharedForms.contextual : Checkpoints.sharedForms.administration,
    page: context.pages.page, target: context.pages.sharedForms.target() });
};
