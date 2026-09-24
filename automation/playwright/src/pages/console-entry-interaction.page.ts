import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export class ConsoleEntryInteractionPage {
  public constructor(private readonly page: Page) {}

  public async startFromFallback(query: string, entryName: string): Promise<void> {
    await this.page.getByTestId(TestIds.console.commandTrigger).click();
    await this.page.getByTestId(TestIds.console.commandPalette).waitFor({ state: 'visible' });
    await this.page.getByTestId(TestIds.console.commandInput).fill(query);
    const fallback = this.page.getByTestId(TestIds.console.commandFallback).filter({ hasText: entryName });
    await fallback.waitFor({ state: 'visible' });
    await fallback.click();
    await this.interaction.waitFor({ state: 'visible' });
  }

  public userMessage(content: string): Locator {
    return this.page.locator(
      `[data-testid="${TestIds.workplace.conversationMessage}"][data-message-role="user"]`,
    )
      .filter({ hasText: content });
  }

  public get interaction(): Locator { return this.page.getByTestId(TestIds.console.entryInteraction); }
  public get assistantResponse(): Locator {
    return this.page.locator(
      `[data-testid="${TestIds.workplace.conversationMessage}"][data-message-role="agentstration"]`,
    );
  }
}
