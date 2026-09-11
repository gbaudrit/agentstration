import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';

export interface ExposeEntryOnDashboardInput {
  entryName: string;
  entryNamespace?: string;
}

export const exposeEntryOnDashboard: Journey<ExposeEntryOnDashboardInput> = async (context, input) => {
  if (!input.entryName?.trim()) throw new Error("Expose Entry input 'entryName' is required.");
  const entryNamespace = input.entryNamespace?.trim() || 'default';
  const editor = context.pages.dashboardEditor;
  await editor.openWithEntry(context.consoleUrl, input.entryName, entryNamespace);
  await editor.includeEntry(input.entryName, entryNamespace);
  await context.checkpoint({ name: Checkpoints.exposeEntry.selected, page: context.pages.page, target: editor.entryRow(input.entryName, entryNamespace) });
  await editor.publish();
  await context.checkpoint({ name: Checkpoints.exposeEntry.published, page: context.pages.page, target: editor.editor });
};
