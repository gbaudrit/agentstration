import { Checkpoints } from '../contracts/checkpoints.js';
import type { SupportedTestLocale } from '../locales/expected-text.js';
import { CollectionContractPage, type CollectionObservation } from '../pages/collection-contract.page.js';
import type { Journey, JourneyContext } from './journey.js';

export interface InspectCollectionsInput {
  locale?: SupportedTestLocale;
  route?: string;
  requirePopulated?: boolean;
}

export const inspectCollections: Journey<InspectCollectionsInput> = async (context, input) => {
  const collections = new CollectionContractPage(context.pages.page);
  if (input.route) await collections.open(context.consoleUrl, input.route);
  const observation = await collections.inspect(input.locale ?? 'en-US');
  if (input.requirePopulated && observation.identities + observation.frames === 0)
    throw new Error(`The deterministic collection fixture was empty: ${observation.url}`);
  await context.checkpoint({ name: Checkpoints.collections.contract, page: context.pages.page });
};

/** Add the contract to existing stable domain checkpoints, retaining their original assertions. */
export async function withCollectionAssertions<T>(
  context: JourneyContext,
  journey: Journey<T>,
  input: T,
  locale: SupportedTestLocale = 'en-US',
): Promise<CollectionObservation[]> {
  const observations: CollectionObservation[] = [];
  const collections = new CollectionContractPage(context.pages.page);
  await context.pages.page.context().grantPermissions(['clipboard-read', 'clipboard-write']);
  await journey({ ...context, checkpoint: async checkpoint => {
    observations.push(await collections.inspect(locale));
    await context.checkpoint(checkpoint);
  } }, input);
  observations.push(await collections.inspect(locale));
  return observations;
}
