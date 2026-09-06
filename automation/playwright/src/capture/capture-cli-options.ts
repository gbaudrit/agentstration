import type { CapturePlan } from './capture-plan.js';
import { normalizeProductUrl } from '../fixtures/product-addresses.js';

export interface CaptureCliOptions {
  planFile: string;
  outputDirectory: string;
  consoleUrl?: string;
  workplaceUrl?: string;
}

export interface CaptureAddresses {
  consoleUrl: string;
  workplaceUrl: string;
}

const supportedArguments = new Set(['plan', 'output', 'console-url', 'workplace-url']);

export function parseCaptureCliOptions(values: string[]): CaptureCliOptions {
  const argumentsMap = new Map<string, string>();
  for (let index = 0; index < values.length; index += 2) {
    const key = values[index];
    const value = values[index + 1];
    if (!key?.startsWith('--') || !value) {
      throw new Error('Expected --plan <file> --output <directory> [--console-url <url>] [--workplace-url <url>].');
    }

    const name = key.slice(2);
    if (!supportedArguments.has(name)) throw new Error(`Unknown argument '--${name}'.`);
    if (argumentsMap.has(name)) throw new Error(`Argument '--${name}' was supplied more than once.`);
    argumentsMap.set(name, value);
  }

  return {
    planFile: required(argumentsMap, 'plan'),
    outputDirectory: required(argumentsMap, 'output'),
    consoleUrl: argumentsMap.get('console-url'),
    workplaceUrl: argumentsMap.get('workplace-url'),
  };
}

export function resolveCaptureAddresses(options: CaptureCliOptions, plan: CapturePlan): CaptureAddresses | undefined {
  const consoleUrl = options.consoleUrl ?? plan.consoleUrl;
  const workplaceUrl = options.workplaceUrl ?? (options.consoleUrl ? undefined : plan.workplaceUrl);
  if (!consoleUrl && workplaceUrl) throw new Error('A Workplace URL requires --console-url or consoleUrl in the plan.');
  if (!consoleUrl) return undefined;

  const normalizedConsoleUrl = normalizeProductUrl(consoleUrl, 'Console');
  return {
    consoleUrl: normalizedConsoleUrl,
    workplaceUrl: workplaceUrl ? normalizeProductUrl(workplaceUrl, 'Workplace') : normalizedConsoleUrl,
  };
}

function required(values: Map<string, string>, name: string): string {
  const value = values.get(name);
  if (!value) throw new Error(`--${name} is required.`);
  return value;
}
