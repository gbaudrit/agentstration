import type { Locator, Page } from '@playwright/test';
import type { ProductAddresses } from '../fixtures/product-hosts.js';
import type { ProductPages } from '../pages/product.pages.js';

export interface JourneyCheckpoint {
  name: string;
  page: Page;
  target?: Locator;
}

export interface JourneyContext extends ProductAddresses {
  pages: ProductPages;
  checkpoint(checkpoint: JourneyCheckpoint): Promise<void>;
}

export type Journey<TInput = unknown> = (context: JourneyContext, input: TInput) => Promise<void>;

export const ignoreCheckpoints = async (): Promise<void> => {};
