import { authenticateConsole } from './authenticate-console.journey.js';
import type { Journey } from './journey.js';

export const journeys: Readonly<Record<string, Journey<Record<string, unknown>>>> = {
  'authenticate-console': authenticateConsole as Journey<Record<string, unknown>>,
};
