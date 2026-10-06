import { test } from '@playwright/test';
import { prepare, navigationAndPreferences, sectionHistoryAndModeControls, secureLaunch, unavailableHandler } from './scenarios.mjs';
const base = process.env.WEBLAUNCH_TEST_URL || 'http://localhost:5148/WebLaunch/';
test('browser launch and navigation', async ({ page }) => {
    await prepare(page, base);
    await navigationAndPreferences(page, base);
    await sectionHistoryAndModeControls(page, base);
    await secureLaunch(page, base);
    await unavailableHandler(page, base);
});
