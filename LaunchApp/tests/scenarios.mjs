import { expect } from '@playwright/test';

export async function prepare(page, base) {
    await page.addInitScript(() => {
        Object.defineProperty(navigator, 'userAgent', { get: () => 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0.0.0 Safari/537.36' });
    });
    await page.goto(base);
    await expect(page.getByRole('heading', { name: 'Your game library' })).toBeVisible();
}
export async function navigationAndPreferences(page, base) {
    await page.evaluate(() => localStorage.setItem('unrelated-app', 'keep'));
    await page.getByRole('button', { name: 'Toggle dark theme' }).click();
    const theme = await page.locator('html').getAttribute('data-theme');
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    expect(await page.evaluate(() => localStorage.getItem('unrelated-app'))).toBe('keep');
    await page.getByRole('link', { name: 'Open Final Fantasy XIV', exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(page.getByRole('heading', { level: 1, name: 'Final Fantasy XIV' })).toBeVisible();
    await page.getByRole('link', { name: 'Story', exact: true }).click();
    await expect(page).toHaveURL(base + 'counter#story');
    await page.goto(base + 'counter?section=world');
    await expect(page).toHaveURL(base + 'counter#world');
    await page.getByRole('link', { name: 'Spellborn', exact: true }).click();
    await expect(page.getByRole('link', { name: 'Classes', exact: true })).toBeVisible();
    await page.getByRole('link', { name: 'Classes', exact: true }).click();
    await expect(page).toHaveURL(base + 'spellborn#classes');
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(base + 'counter');
    await expect(page.getByRole('button', { name: 'Connect', exact: true })).toBeVisible();
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await page.locator('meta[name="viewport"]').getAttribute('content')).not.toContain('user-scalable=no');
    await page.setViewportSize({ width: 1280, height: 900 });
}
async function pair(page) {
    await page.getByRole('button', { name: 'Forget this browser pairing' }).click();
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.pairing-code')).toHaveText(/^\d{6}$/);
    await expect(page.getByRole('button', { name: 'The codes match' })).toBeFocused();
    await page.getByRole('button', { name: 'The codes match' }).click();
    await expect(page.locator('.launch-status')).toHaveText('Paired. Enter your game settings.');
}
export async function secureLaunch(page, base) {
    await page.goto(base + 'counter');
    await pair(page);
    const consoles = []; page.on('console', message => consoles.push(message.text()));
    await page.getByLabel('Installation folder').fill('C:\\Games\\日本語');
    await page.getByLabel('Username', { exact: true }).fill('synthetic-user');
    await page.getByLabel('Password', { exact: true }).fill('synthetic-secret:+?=é');
    await page.getByLabel('One-time password (optional)').fill('12');
    await page.getByRole('button', { name: 'Launch game', exact: true }).click();
    expect(await page.getByLabel('One-time password (optional)').evaluate(element => element.validity.valid)).toBe(false);
    await page.getByLabel('One-time password (optional)').fill('');
    await page.getByRole('button', { name: 'Launch game', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Cancel launch' })).toBeVisible();
    await expect(page.getByLabel('Password', { exact: true })).toHaveValue('');
    await expect(page.locator('.launch-status')).toHaveText('Game process started.', { timeout: 15000 });
    expect(page.url()).not.toContain('synthetic');
    expect(consoles.join('\n')).not.toContain('synthetic-secret');
    expect(await page.evaluate(() => JSON.stringify(localStorage))).not.toContain('synthetic-secret');
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.launch-status')).toHaveText('Connected. Enter your game settings.');
    await page.getByLabel('Password', { exact: true }).fill('synthetic-cancel');
    await page.getByRole('button', { name: 'Launch game', exact: true }).click();
    await page.getByRole('button', { name: 'Cancel launch' }).click();
    await expect(page.locator('.launch-status')).toHaveText('Launch cancelled.', { timeout: 15000 });
}
export async function unavailableHandler(page, base) {
    await page.goto(base + 'spellborn');
    await page.route('http://127.0.0.1:47832/**', route => route.abort());
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.launch-status')).toContainText('Cannot connect to WebLaunch');
    await expect(page.getByRole('button', { name: 'Launch game', exact: true })).toBeDisabled();
    await page.unroute('http://127.0.0.1:47832/**');
}
