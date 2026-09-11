import path from 'node:path';
import { fileURLToPath } from 'node:url';

const currentDirectory = path.dirname(fileURLToPath(import.meta.url));

export const repositoryRoot = path.resolve(
  process.env.AGENTSTRATION_REPOSITORY ?? path.join(currentDirectory, '../../../..'),
);

export const automationRoot = path.join(repositoryRoot, 'automation', 'playwright');
