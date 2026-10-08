import { chromium } from '@playwright/test';
import { execFileSync } from 'node:child_process';
import { prepare, navigationAndPreferences, sectionHistoryAndModeControls, secureLaunch, unavailableHandler, settingsDialogs, browserRecovery } from './scenarios.mjs';
const cdp = execFileSync('coderabbit-agent-browser', ['get', 'cdp-url'], { encoding: 'utf8' }).trim();
const browser = await chromium.connectOverCDP(cdp);
const page = browser.contexts()[0].pages()[0];
const base = process.env.WEBLAUNCH_TEST_URL || 'http://localhost:5148/';
try {
    await prepare(page, base);
    await navigationAndPreferences(page, base);
    await sectionHistoryAndModeControls(page, base);
    await settingsDialogs(page, base);
    await secureLaunch(page, base);
    await unavailableHandler(page, base);
    await browserRecovery(page, base);
    console.log('Shared Chromium: navigation, theme, mobile, pairing, encrypted launch, cancellation, and unavailable handler passed.');
} finally { await browser.close(); }
