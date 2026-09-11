export const Checkpoints = {
  console: {
    home: 'console-home',
  },
  createAgent: {
    formEmpty: 'agent-form-empty',
    identityComplete: 'agent-identity-complete',
    behaviorComplete: 'agent-behavior-complete',
    readyToCreate: 'agent-ready-to-create',
    created: 'agent-created',
  },
  createFlow: {
    formInitial: 'flow-form-initial',
    identityComplete: 'flow-identity-complete',
    participantsComplete: 'flow-participants-complete',
    strategyComplete: 'flow-strategy-complete',
    created: 'flow-created',
    published: 'flow-published',
  },
  createEntry: {
    formInitial: 'entry-form-initial',
    identityComplete: 'entry-identity-complete',
    appearanceComplete: 'entry-appearance-complete',
    interactionComplete: 'entry-interaction-complete',
    fieldComplete: 'entry-field-complete',
    bindingComplete: 'entry-binding-complete',
    behaviorComplete: 'entry-behavior-complete',
    suggestionsComplete: 'entry-suggestions-complete',
    readyToPublish: 'entry-ready-to-publish',
    published: 'entry-published',
  },
  createWorkspace: {
    formEmpty: 'workspace-form-empty',
    identityComplete: 'workspace-identity-complete',
    created: 'workspace-created',
    selected: 'workspace-selected',
  },
  workplace: {
    home: 'workplace-home',
    entryReady: 'workplace-entry-ready',
    requestSubmitted: 'workplace-request-submitted',
    responseCompleted: 'workplace-response-completed',
  },
} as const;
