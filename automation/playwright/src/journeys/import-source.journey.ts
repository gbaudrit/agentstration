import { Checkpoints } from '../contracts/checkpoints.js';
import { TestIds } from '../contracts/test-ids.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ImportSourceInput {
  publisher: string;
  name: string;
  version?: string;
  displayName: string;
  username?: string;
  password?: string;
}

export const importSource: Journey<ImportSourceInput> = async (context, input) => {
  validate(input);
  await prepareConsoleJourney(context, input);
  await context.pages.distribution.open(context.consoleUrl, '/settings/sources', 'sources');
  await context.checkpoint({ name: Checkpoints.importSource.form, page: context.pages.page, target: context.pages.page.getByTestId(TestIds.distribution.sources) });
  await context.pages.distribution.importSource(context.consoleUrl, input.publisher, input.name, manifest(input));
  await context.checkpoint({ name: Checkpoints.importSource.imported, page: context.pages.page, target: context.pages.distribution.sourceDetails(input.publisher, input.name) });
};

function manifest(input: ImportSourceInput): string {
  return `apiVersion: agentstration.io/v1
kind: SourceVersion
metadata:
  name: ${input.name}
definition:
  version: "${input.version ?? '1'}"
  displayName: ${input.displayName}
  publisher:
    name: ${input.publisher}
  bindings: []
  channels: []
  catalogs: []
`;
}

function validate(input: ImportSourceInput): void {
  for (const key of ['publisher', 'name', 'displayName'] as const) {
    if (!input[key]?.trim()) throw new Error(`Import Source input '${key}' is required.`);
    if (key !== 'displayName' && !/^[a-z0-9][a-z0-9-]*$/.test(input[key])) throw new Error(`Import Source input '${key}' must be a lowercase resource name.`);
  }
}
