export function normalizeProductUrl(value: string, label: string): string {
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
