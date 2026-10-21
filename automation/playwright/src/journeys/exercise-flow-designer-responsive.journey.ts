import { Checkpoints } from '../contracts/checkpoints.js';
import type { Journey } from './journey.js';
import { prepareConsoleJourney } from './prepare-console.journey.js';

export interface ExerciseFlowDesignerResponsiveInput {
  username?: string;
  password?: string;
}

export const exerciseFlowDesignerResponsive: Journey<ExerciseFlowDesignerResponsiveInput> = async (context, input) => {
  await prepareConsoleJourney(context, input);
  const designer = context.pages.flowDesigner;

  await designer.useDesktopViewport();
  await designer.createResponsiveDraftAndOpen(context.consoleUrl);
  await designer.expectPanels(true, true);
  await designer.expectWorkspaceControlsWithinViewport();

  await designer.toggleDesktopPalette();
  await designer.toggleDesktopInspector();
  await designer.expectPanels(false, false);
  await designer.enterFocusMode();
  await designer.expectPanels(false, false);
  await designer.exitFocusModeWithToolbar();
  await designer.expectPanels(false, false);

  await designer.toggleDesktopPalette();
  await designer.toggleDesktopInspector();
  await designer.expectPanels(true, true);
  await context.checkpoint({
    name: Checkpoints.flowDesignerResponsive.desktopWorkspace,
    page: context.pages.page,
    target: designer.designer,
  });

  await designer.useNarrowViewport();
  await designer.expectPanels(false, false);
  await designer.expectWorkspaceControlsWithinViewport();
  await designer.openNarrowPalette();
  await designer.expectWorkspaceControlsWithinViewport();
  await designer.openNarrowInspector();
  await context.checkpoint({
    name: Checkpoints.flowDesignerResponsive.narrowDrawers,
    page: context.pages.page,
    target: designer.designer,
  });

  await designer.enterFocusMode();
  await designer.expectPanels(false, true);
  await designer.expectWorkspaceControlsWithinViewport(true);
  await designer.selectFocusMode('definition');
  await designer.selectFocusMode('split');
  await designer.expectPanels(false, true);
  await designer.selectFocusMode('designer');
  await context.checkpoint({
    name: Checkpoints.flowDesignerResponsive.focusModes,
    page: context.pages.page,
    target: designer.designer,
  });
  await designer.exitFocusModeWithEscape();
  await designer.expectPanels(false, true);

  await designer.runCollapsedVerificationExpecting(0);
  await designer.collapseValidation();
  await designer.openNarrowPalette();
  await designer.addUnreachableTransform();
  await designer.runCollapsedVerificationExpecting(1);
  await designer.expectValidationIssue('step_unreachable');
  await context.checkpoint({
    name: Checkpoints.flowDesignerResponsive.verificationFailure,
    page: context.pages.page,
    target: designer.designer,
  });
};
