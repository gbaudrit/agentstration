import type { Locator, Page } from '@playwright/test';
import { TestIds } from '../contracts/test-ids.js';

export interface WorkplaceRouteContext {
  workspaceName: string;
  dashboardName: string;
}

export interface WorkplaceConversation {
  conversationId: string;
  taskId?: string;
}

export class WorkplacePage {
  public constructor(private readonly page: Page) {}

  public async openRoot(workplaceUrl: string): Promise<WorkplaceRouteContext> {
    await this.open(`${workplaceUrl}/`);
    await this.page.waitForURL(url => /^\/w\/[^/]+\/d\/[^/]+$/.test(url.pathname));
    await this.home.waitFor({ state: 'visible' });
    return routeContext(this.page.url());
  }

  public async openWorkspace(workplaceUrl: string, workspaceName: string): Promise<WorkplaceRouteContext> {
    await this.open(`${workplaceUrl}/w/${encodeURIComponent(workspaceName)}`);
    await this.page.waitForURL(url => url.pathname.startsWith(`/w/${encodeURIComponent(workspaceName)}/d/`));
    await this.home.waitFor({ state: 'visible' });
    return routeContext(this.page.url());
  }

  public async openDashboard(workplaceUrl: string, context: WorkplaceRouteContext): Promise<void> {
    await this.open(`${workplaceUrl}${dashboardPath(context)}`);
    await this.home.waitFor({ state: 'visible' });
  }

  public async openEntry(
    workplaceUrl: string,
    context: WorkplaceRouteContext,
    entryNamespace: string,
    entryName: string,
  ): Promise<void> {
    await this.open(`${workplaceUrl}${dashboardPath(context)}/start/${encodeURIComponent(entryNamespace)}/${encodeURIComponent(entryName)}`);
    await this.page.locator(
      `[data-testid="${TestIds.workplace.entryStart}"][data-entry-id="${attributeValue(entryName)}"][data-entry-namespace="${attributeValue(entryNamespace)}"]`,
    ).waitFor({ state: 'visible' });
  }

  public async submitPrompt(content: string): Promise<WorkplaceConversation> {
    await this.page.getByTestId(TestIds.workplace.promptInput).fill(content);
    await Promise.all([
      this.page.waitForURL(url => /\/conversations\/[0-9a-f-]{36}$/i.test(url.pathname)),
      this.page.getByTestId(TestIds.workplace.promptSubmit).click(),
    ]);
    await this.interaction.waitFor({ state: 'visible' });
    const conversationId = lastPathSegment(this.page.url());
    const inlineTask = this.page.getByTestId(TestIds.workplace.inlineTask);
    const taskId = await inlineTask.count() > 0 ? await inlineTask.first().getAttribute('data-task-id') : null;
    return { conversationId, ...(taskId ? { taskId } : {}) };
  }

  public async submitForm(fieldName: string, content: string): Promise<WorkplaceConversation> {
    const field = this.page.locator(
      `[data-testid="${TestIds.workplace.formInput}"][data-field-name="${attributeValue(fieldName)}"]`,
    );
    await field.locator('input, textarea, select').fill(content);
    await Promise.all([
      this.page.waitForURL(url => /\/conversations\/[0-9a-f-]{36}$/i.test(url.pathname)),
      this.page.getByTestId(TestIds.workplace.formSubmit).click(),
    ]);
    await this.interaction.waitFor({ state: 'visible' });
    return { conversationId: lastPathSegment(this.page.url()) };
  }

  public async waitForAgentResponse(): Promise<void> {
    await this.page.locator(
      `[data-testid="${TestIds.workplace.conversationMessage}"][data-message-role="agentstration"]`,
    ).or(this.page.getByTestId(TestIds.workplace.assistantResponse)).last().waitFor({ state: 'visible' });
  }

  public async waitForInlineTask(): Promise<string> {
    const task = this.page.getByTestId(TestIds.workplace.inlineTask).first();
    await task.waitFor({ state: 'visible' });
    const taskId = await task.getAttribute('data-task-id');
    if (!taskId) throw new Error('The Workplace task card does not expose its task identifier.');
    return taskId;
  }

  public async openConversation(workplaceUrl: string, context: WorkplaceRouteContext, conversationId: string): Promise<void> {
    await this.open(`${workplaceUrl}${dashboardPath(context)}/conversations/${encodeURIComponent(conversationId)}`);
    await this.interaction.waitFor({ state: 'visible' });
  }

  public async openActivity(workplaceUrl: string, workspaceName: string): Promise<void> {
    await this.open(`${workplaceUrl}/w/${encodeURIComponent(workspaceName)}/tasks`);
    await this.page.getByTestId(TestIds.workplace.activity).waitFor({ state: 'visible' });
  }

  public async showTasks(): Promise<void> {
    await this.page.getByTestId(TestIds.workplace.tasksTab).click();
    await this.page.getByTestId(TestIds.workplace.tasksTab).waitFor({ state: 'visible' });
  }

  public async waitForConversation(conversationId: string): Promise<void> {
    await this.page.locator(
      `[data-testid="${TestIds.workplace.conversationCard}"][data-conversation-id="${attributeValue(conversationId)}"]`,
    ).waitFor({ state: 'visible' });
  }

  public async waitForTask(taskId: string): Promise<void> {
    await this.page.locator(
      `[data-testid="${TestIds.workplace.taskCard}"][data-task-id="${attributeValue(taskId)}"]`,
    ).waitFor({ state: 'visible' });
  }

  public async openTask(workplaceUrl: string, workspaceName: string, taskId: string): Promise<void> {
    await this.open(`${workplaceUrl}/w/${encodeURIComponent(workspaceName)}/tasks/${encodeURIComponent(taskId)}`);
    await this.page.locator(
      `[data-testid="${TestIds.workplace.taskDetails}"][data-task-id="${attributeValue(taskId)}"]`,
    ).waitFor({ state: 'visible' });
  }

  public async openNotifications(workplaceUrl: string, workspaceName: string): Promise<void> {
    await this.open(`${workplaceUrl}/w/${encodeURIComponent(workspaceName)}/notifications`);
    await this.page.getByTestId(TestIds.workplace.notifications).waitFor({ state: 'visible' });
  }

  public async markFirstNotificationRead(): Promise<void> {
    const unread = this.page.locator(
      `[data-testid="${TestIds.workplace.notification}"][data-read="false"]`,
    ).first();
    await unread.waitFor({ state: 'visible' });
    const notificationId = await unread.getAttribute('data-notification-id');
    await unread.getByTestId(TestIds.workplace.markRead).click();
    if (notificationId) {
      await this.page.locator(
        `[data-testid="${TestIds.workplace.notification}"][data-notification-id="${attributeValue(notificationId)}"][data-read="true"]`,
      ).waitFor({ state: 'visible' });
    }
  }

  public async toggleTheme(): Promise<void> {
    const shell = this.shell;
    const wasDark = await shell.evaluate(element => element.classList.contains('theme-dark'));
    await this.page.getByTestId(TestIds.workplace.themeToggle).click();
    await this.page.locator(
      `[data-testid="${TestIds.workplace.shell}"].theme-${wasDark ? 'light' : 'dark'}`,
    ).waitFor({ state: 'visible' });
  }

  public get shell(): Locator { return this.page.getByTestId(TestIds.workplace.shell); }
  public get home(): Locator { return this.page.getByTestId(TestIds.workplace.home); }
  public get interaction(): Locator { return this.page.getByTestId(TestIds.workplace.interaction); }
  public get conversationThread(): Locator { return this.page.getByTestId(TestIds.workplace.conversationThread); }
  public get realtimeStatus(): Locator { return this.page.getByTestId(TestIds.workplace.realtimeStatus).first(); }
  public get mobileAppBar(): Locator { return this.page.getByTestId(TestIds.workplace.mobileAppBar); }
  public get recentConversations(): Locator { return this.page.getByTestId(TestIds.workplace.recentConversation); }

  public recentConversation(conversationId: string): Locator {
    return this.page.locator(
      `[data-testid="${TestIds.workplace.recentConversation}"][data-conversation-id="${attributeValue(conversationId)}"]`,
    );
  }

  private async open(url: string): Promise<void> {
    const response = await this.page.goto(url, { waitUntil: 'domcontentloaded' });
    if (!response?.ok()) throw new Error(`Workplace page '${url}' returned HTTP ${response?.status() ?? 'no response'}.`);
    await this.shell.waitFor({ state: 'visible' });
  }
}

function dashboardPath(context: WorkplaceRouteContext): string {
  return `/w/${encodeURIComponent(context.workspaceName)}/d/${encodeURIComponent(context.dashboardName)}`;
}

function routeContext(url: string): WorkplaceRouteContext {
  const segments = new URL(url).pathname.split('/').filter(Boolean);
  if (segments.length < 4 || segments[0] !== 'w' || segments[2] !== 'd') {
    throw new Error(`Expected a canonical Workplace dashboard URL, received '${url}'.`);
  }
  return { workspaceName: decodeURIComponent(segments[1]!), dashboardName: decodeURIComponent(segments[3]!) };
}

function lastPathSegment(url: string): string {
  const value = new URL(url).pathname.split('/').filter(Boolean).at(-1);
  if (!value) throw new Error(`The URL '${url}' does not contain a resource identifier.`);
  return decodeURIComponent(value);
}

function attributeValue(value: string): string {
  if (!/^[a-zA-Z0-9_.:/-]+$/.test(value)) throw new Error(`Unsupported identifier '${value}' in a browser locator.`);
  return value;
}
