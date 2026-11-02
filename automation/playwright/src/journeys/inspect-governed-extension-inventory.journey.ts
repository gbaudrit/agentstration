import { Checkpoints } from '../contracts/checkpoints.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney, type ConsoleJourneySetup } from './prepare-console.journey.js';

export interface InspectGovernedExtensionInventoryInput extends ConsoleJourneySetup {
  locale?: SupportedTestLocale;
}

export const inspectGovernedExtensionInventory: Journey<InspectGovernedExtensionInventoryInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const locale = input.locale ?? 'en-US';
  await context.pages.consoleAdministration.restoreLanguage(context.consoleUrl, locale);
  const inventory = context.pages.governedExtensionInventory;
  await inventory.openFromCatalog(context.consoleUrl);
  await inventory.inspectInventory(locale);
  await context.checkpoint({ name: Checkpoints.governedInventory.inventory, page: context.pages.page, target: inventory.inventory });
  await inventory.inspectTechnicalDetails(locale);
  await context.checkpoint({ name: Checkpoints.governedInventory.technical, page: context.pages.page, target: inventory.inventory });
  await inventory.inspectManagedResources(context.consoleUrl, locale);
  await context.checkpoint({ name: Checkpoints.governedInventory.protected, page: context.pages.page, target: inventory.managed });
  await inventory.inspectContributionsAndTools(context.consoleUrl, locale);
  await context.checkpoint({ name: Checkpoints.governedInventory.contributions, page: context.pages.page, target: inventory.contributions });
  await inventory.inspectOrdinaryResources(context.consoleUrl, locale);
  await context.checkpoint({ name: Checkpoints.governedInventory.ordinary, page: context.pages.page });
  await inventory.inspectDegraded(context.consoleUrl, locale);
  await context.checkpoint({ name: Checkpoints.governedInventory.degraded, page: context.pages.page, target: inventory.inventory });
};
