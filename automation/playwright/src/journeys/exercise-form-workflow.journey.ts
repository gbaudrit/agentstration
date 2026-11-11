import { randomUUID } from 'node:crypto';
import { Checkpoints } from '../contracts/checkpoints.js';
import type { FormAppearance } from '../contracts/shared-form-cases.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { FormWorkflowsPage } from '../pages/form-workflows.page.js';
import { importSource } from './import-source.journey.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseFormWorkflowInput {
  kind: 'flow-inspector' | 'source-scheduling' | 'entry-form' | 'workplace-prompt';
  locale: SupportedTestLocale;
  theme: FormAppearance;
}
export const exerciseFormWorkflow: Journey<ExerciseFormWorkflowInput> = async (context, input) => {
  await prepareConsoleJourney(context, {});
  await context.pages.sharedForms.configure(context.consoleUrl, input.locale, input.theme);
  const forms = new FormWorkflowsPage(context.pages.page);
  const suffix = randomUUID().slice(0, 8);
  switch (input.kind) {
    case 'workplace-prompt':
      await context.pages.sharedForms.exerciseWorkplacePrompt(context.workplaceUrl, input.locale, input.theme);
      break;
    case 'flow-inspector': {
      const name = await context.pages.flowDesigner.createResponsiveDraftAndOpen(context.consoleUrl);
      await context.pages.flowDesigner.selectTransition('input —completed→ completed');
      await forms.exerciseTransitionPriority(context.consoleUrl, name, input.locale);
      break;
    }
    case 'source-scheduling': {
      const name = `forms-schedule-${suffix}`;
      await importSource({ ...context, theme: input.theme }, { publisher: 'forms', name, displayName: 'Source scheduling fixture' });
      await forms.exerciseSourceScheduling(context.consoleUrl, 'forms', name, input.locale);
      break;
    }
    case 'entry-form': {
      const name = `forms-fields-${suffix}`;
      await forms.provisionEntry(context.consoleUrl, name);
      await forms.exerciseEntry(context.workplaceUrl, name, input.locale);
      break;
    }
  }
  await context.checkpoint({ name: input.kind === 'flow-inspector' ? Checkpoints.sharedForms.inspector : Checkpoints.sharedForms.dynamic,
    page: context.pages.page });
};
