const ignoredConsoleErrors = [
  /^Failed to load resource: the server responded with a status of 404 \(Not Found\)$/,
] as const;

export function isActionableConsoleError(type: string, message: string): boolean {
  if (type !== 'error') return false;
  return !ignoredConsoleErrors.some(pattern => pattern.test(message.trim()));
}

export function isActionableFirstPartyFailure(
  requestUrl: string,
  firstPartyOrigins: ReadonlySet<string>,
  failureReason: string,
): boolean {
  let origin: string;
  try {
    origin = new URL(requestUrl).origin;
  } catch {
    return false;
  }

  return firstPartyOrigins.has(origin) && !failureReason.includes('ERR_ABORTED');
}

export function isActionableAssetResponse(
  requestUrl: string,
  firstPartyOrigins: ReadonlySet<string>,
  resourceType: string,
  status: number,
): boolean {
  if (status < 400 || (resourceType !== 'stylesheet' && resourceType !== 'script')) return false;
  try {
    return firstPartyOrigins.has(new URL(requestUrl).origin);
  } catch {
    return false;
  }
}
