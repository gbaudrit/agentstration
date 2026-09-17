import type { Locator } from '@playwright/test';

export type ControlSelection = string | readonly string[];

export async function fillAndCommit(locator: Locator, value: string): Promise<void> {
  await locator.fill(value);
  await locator.blur();
}

export async function selectFirstAvailable(locator: Locator, selection: ControlSelection, label: string): Promise<void> {
  await locator.waitFor({ state: 'visible' });
  const candidates = typeof selection === 'string' ? [selection] : [...selection];
  const options = await locator.locator('option').evaluateAll(elements => elements.map(element => ({
    value: (element as HTMLOptionElement).value,
    label: element.textContent?.trim() ?? '',
    disabled: (element as HTMLOptionElement).disabled,
  })));
  const selected = candidates.find(candidate => options.some(option => option.value === candidate && !option.disabled));
  if (!selected) {
    const available = options.filter(option => option.value && !option.disabled).map(option => `${option.value} (${option.label})`);
    throw new Error(`${label} candidates [${candidates.join(', ')}] are unavailable. Available options: ${available.join(', ') || 'none'}.`);
  }
  await locator.selectOption(selected);
}
