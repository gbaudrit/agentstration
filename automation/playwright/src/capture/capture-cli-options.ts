import type { CapturePlan } from './capture-plan.js';

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

  const normalizedConsoleUrl = normalizeHttpUrl(consoleUrl, 'Console');
  return {
    consoleUrl: normalizedConsoleUrl,
    workplaceUrl: workplaceUrl ? normalizeHttpUrl(workplaceUrl, 'Workplace') : normalizedConsoleUrl,
  };
}

function normalizeHttpUrl(value: string, label: string): string {
  let url: URL;
  try {
    url = new URL(value);
  } catch {
    throw new Error(`${label} URL '${value}' is not a valid absolute URL.`);
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    throw new Error(`${label} URL must use http or https.`);
  }
  return value.replace(/\/+$/, '');
}

function required(values: Map<string, string>, name: string): string {
  const value = values.get(name);
  if (!value) throw new Error(`--${name} is required.`);
  return value;
}
