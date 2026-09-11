import { createHash } from 'node:crypto';
import fs from 'node:fs/promises';
import path from 'node:path';
import type { JourneyCheckpoint } from '../journeys/journey.js';
import type { CapturePlan } from './capture-plan.js';

export interface CapturedAsset {
  checkpoint: string;
  file: string;
  sha256: string;
}

export function createScreenshotRecorder(plan: CapturePlan, outputDirectory: string, assets: CapturedAsset[]) {
  const pending = new Map(plan.captures.map(value => [value.checkpoint, value]));
  return async (checkpoint: JourneyCheckpoint): Promise<void> => {
    const request = pending.get(checkpoint.name);
    if (!request) return;

    const destination = path.resolve(outputDirectory, request.file);
    const relative = path.relative(path.resolve(outputDirectory), destination);
    if (relative.startsWith('..') || path.isAbsolute(relative)) {
      throw new Error(`Capture file must stay inside the output directory: ${request.file}`);
    }
    await fs.mkdir(path.dirname(destination), { recursive: true });
    if (request.scope === 'target') {
      if (!checkpoint.target) throw new Error(`Checkpoint ${checkpoint.name} has no target.`);
      await checkpoint.target.screenshot({ path: destination });
    } else {
      await checkpoint.page.screenshot({ path: destination, fullPage: request.fullPage ?? true });
    }
    const bytes = await fs.readFile(destination);
    assets.push({
      checkpoint: checkpoint.name,
      file: request.file,
      sha256: createHash('sha256').update(bytes).digest('hex'),
    });
    pending.delete(checkpoint.name);
  };
}
