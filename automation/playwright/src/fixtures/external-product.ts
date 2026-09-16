import type { ProductAddresses } from './product-hosts.js';
import { normalizeProductUrl } from './product-addresses.js';

export const externalProductEnvironment = {
  consoleUrl: 'AGENTSTRATION_CONSOLE_URL',
  workplaceUrl: 'AGENTSTRATION_WORKPLACE_URL',
} as const;

export function resolveExternalProductAddresses(environment: NodeJS.ProcessEnv): ProductAddresses | undefined {
  const consoleUrl = optional(environment[externalProductEnvironment.consoleUrl]);
  const workplaceUrl = optional(environment[externalProductEnvironment.workplaceUrl]);
  if (!consoleUrl && workplaceUrl) {
    throw new Error(`${externalProductEnvironment.workplaceUrl} requires ${externalProductEnvironment.consoleUrl}.`);
  }
  if (!consoleUrl) return undefined;

  const normalizedConsoleUrl = normalizeProductUrl(consoleUrl, 'Console');
  return {
    consoleUrl: normalizedConsoleUrl,
    workplaceUrl: workplaceUrl ? normalizeProductUrl(workplaceUrl, 'Workplace') : normalizedConsoleUrl,
  };
}

function optional(value: string | undefined): string | undefined {
  const normalized = value?.trim();
  return normalized ? normalized : undefined;
}
