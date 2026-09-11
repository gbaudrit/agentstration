import fs from 'node:fs/promises';

export interface CaptureRequest {
  checkpoint: string;
  file: string;
  scope?: 'page' | 'target';
  fullPage?: boolean;
}

export interface CapturePlan {
  productRef?: string;
  journey: string;
  input?: Record<string, unknown>;
  locale?: string;
  theme?: 'light' | 'dark';
  viewport?: { width: number; height: number };
  consoleUrl?: string;
  workplaceUrl?: string;
  captures: CaptureRequest[];
}

export async function readCapturePlan(file: string): Promise<CapturePlan> {
  const value: unknown = JSON.parse(await fs.readFile(file, 'utf8'));
  if (!value || typeof value !== 'object') throw new Error('Capture plan must be a JSON object.');
  const plan = value as Partial<CapturePlan>;
  if (!plan.journey || typeof plan.journey !== 'string') throw new Error('Capture plan journey is required.');
  if (!Array.isArray(plan.captures) || plan.captures.length === 0) throw new Error('Capture plan must request at least one checkpoint.');
  for (const capture of plan.captures) {
    if (!capture.checkpoint || !capture.file) throw new Error('Every capture requires checkpoint and file values.');
  }
  if (!plan.consoleUrl && plan.workplaceUrl) throw new Error('workplaceUrl requires consoleUrl.');
  return plan as CapturePlan;
}
