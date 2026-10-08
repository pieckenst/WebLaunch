import { defineConfig } from '@playwright/test';
export default defineConfig({ testDir: './tests', testMatch: '**/*.spec.mjs', timeout: 90000, workers: 1, use: { browserName: 'chromium', viewport: { width: 1280, height: 900 }, trace: 'retain-on-failure' } });
