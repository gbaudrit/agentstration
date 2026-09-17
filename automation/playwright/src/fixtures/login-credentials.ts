export interface LoginCredentials {
  username: string;
  password: string;
}

export const loginCredentialEnvironment = {
  username: 'AGENTSTRATION_USERNAME',
  password: 'AGENTSTRATION_PASSWORD',
} as const;

export function resolveLoginCredentials(
  explicitUsername: string | undefined,
  explicitPassword: string | undefined,
  environment: NodeJS.ProcessEnv = process.env,
): LoginCredentials {
  const hasExplicitCredentials = explicitUsername !== undefined || explicitPassword !== undefined;
  if (hasExplicitCredentials) {
    if (!explicitUsername || !explicitPassword) throw new Error('Journey login credentials require both username and password.');
    return { username: explicitUsername, password: explicitPassword };
  }

  const username = optional(environment[loginCredentialEnvironment.username]);
  const password = optional(environment[loginCredentialEnvironment.password]);
  if (username || password) {
    if (!username || !password) {
      throw new Error(`${loginCredentialEnvironment.username} and ${loginCredentialEnvironment.password} must be provided together.`);
    }
    return { username, password };
  }

  if (optional(environment.AGENTSTRATION_CONSOLE_URL)) {
    throw new Error(
      `External Playwright instances require ${loginCredentialEnvironment.username} and ${loginCredentialEnvironment.password}.`,
    );
  }

  return { username: 'admin', password: 'admin' };
}

function optional(value: string | undefined): string | undefined {
  return value && value.length > 0 ? value : undefined;
}
