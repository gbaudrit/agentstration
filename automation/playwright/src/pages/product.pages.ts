import type { Page } from '@playwright/test';
import { LoginPage } from './login.page.js';

export class ProductPages {
  public readonly login: LoginPage;

  public constructor(public readonly page: Page) {
    this.login = new LoginPage(page);
  }
}
