import { Checkpoints } from '../contracts/checkpoints.js';
import type { SharedFormCase, FormAppearance } from '../contracts/shared-form-cases.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseSharedFormInput {
  definition: SharedFormCase;
  locale: SupportedTestLocale;
  theme: FormAppearance;
  username?: string;
  password?: string;
}

export const exerciseSharedForm: Journey<ExerciseSharedFormInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const forms = context.pages.sharedForms;
  await forms.configure(context.consoleUrl, input.locale, input.theme);
  await forms.openEditor(context.consoleUrl, input.definition, input.locale, input.theme);
  await forms.assertAssociations();
  await forms.exerciseEditorClearing(input.locale);
  await context.checkpoint({ name: Checkpoints.sharedForms.editorCleared, page: context.pages.page, target: forms.target() });
};
