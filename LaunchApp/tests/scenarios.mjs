import { expect } from '@playwright/test';

async function openDesktop(page) {
    await page.locator('.connection-summary').click();
    await expect(page.getByRole('dialog')).toBeVisible();
}
async function gameSettings(page) {
    await page.getByRole('dialog').getByRole('button', { name: 'Game settings', exact: true }).click();
}
async function desktopSettings(page) {
    await page.getByRole('button', { name: 'Desktop connection', exact: true }).click();
}

export async function prepare(page, base) {
    await page.addInitScript(() => {
        Object.defineProperty(navigator, 'userAgent', { get: () => 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) Chrome/140.0.0.0 Safari/537.36' });
    });
    await page.goto(base);
    await expect(page.getByRole('heading', { name: 'Your game library' })).toBeVisible();
}
export async function navigationAndPreferences(page, base) {
    await page.evaluate(() => localStorage.setItem('unrelated-app', 'keep'));
    await page.getByRole('checkbox', { name: 'Dark theme', exact: true }).focus();
    await page.keyboard.press('Space');
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
    await page.getByRole('link', { name: 'Skip to content', exact: true }).focus();
    await page.keyboard.press('Enter');
    await expect(page).toHaveURL(base + 'spellborn#main');
    await expect(page.getByRole('heading', { level: 1, name: 'Chronicles of Spellborn' })).toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.goto(base + 'counter');
    await openDesktop(page);
    await expect(page.getByRole('button', { name: 'Connect', exact: true })).toBeVisible();
    await page.keyboard.press('Escape');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    expect(await page.locator('meta[name="viewport"]').getAttribute('content')).not.toContain('user-scalable=no');
    await page.setViewportSize({ width: 1280, height: 900 });
}
export async function sectionHistoryAndModeControls(page, base) {
    await page.goto(base + 'counter#story');
    const sections = () => page.getByRole('navigation', { name: 'Game sections', exact: true });
    const selected = name => sections().getByRole('link', { name, exact: true });
    await expect(selected('Story')).toHaveAttribute('aria-current', 'location');
    await page.reload();
    await expect(selected('Story')).toHaveAttribute('aria-current', 'location');
    await expect.poll(() => page.locator('#story').evaluate(element => element.getBoundingClientRect().top)).toBeGreaterThan(0);
    await expect.poll(() => page.locator('#story').evaluate(element => element.getBoundingClientRect().top)).toBeLessThan(260);
    await selected('World').click();
    await expect(page).toHaveURL(base + 'counter#world');
    await expect(selected('World')).toHaveAttribute('aria-current', 'location');
    await page.goBack();
    await expect(page).toHaveURL(base + 'counter#story');
    await expect(selected('Story')).toHaveAttribute('aria-current', 'location');
    await page.goForward();
    await expect(selected('World')).toHaveAttribute('aria-current', 'location');
    await selected('Launch settings').focus(); await page.keyboard.press('Enter');
    await expect(page).toHaveURL(base + 'counter#launch');
    await expect(selected('Launch settings')).toHaveAttribute('aria-current', 'location');
    await openDesktop(page);
    await page.getByLabel('Desktop launcher mode', { exact: true }).selectOption('console');
    await expect(page.getByRole('link', { name: 'Open desktop launcher', exact: true })).toHaveAttribute('href', 'HandleWebRequest:connect?v=2&mode=console');
    await page.reload();
    await openDesktop(page);
    await expect(page.getByLabel('Desktop launcher mode', { exact: true })).toHaveValue('console');
    await page.getByLabel('Desktop launcher mode', { exact: true }).selectOption('gui');
    await expect(page.getByRole('link', { name: 'Open desktop launcher', exact: true })).toHaveAttribute('href', 'HandleWebRequest:connect?v=2&mode=gui');
    await page.goto(base + 'spellborn/classes');
    await expect(page).toHaveURL(base + 'spellborn#classes');
    await expect(selected('Classes')).toHaveAttribute('aria-current', 'location');
    await page.goto(base + 'spellborn?section=overview');
    await expect(page).toHaveURL(base + 'spellborn#overview');
    await expect(selected('Overview')).toHaveAttribute('aria-current', 'location');
    await page.goto(base + 'counter/not-a-section');
    await expect(page).toHaveURL(base + 'counter#overview');
    await expect(selected('Overview')).toHaveAttribute('aria-current', 'location');
    await page.setViewportSize({ width: 390, height: 844 });
    await selected('Features').click();
    await expect(page).toHaveURL(base + 'counter#features');
    await expect(selected('Features')).toHaveAttribute('aria-current', 'location');
    expect(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth)).toBe(true);
    await page.getByText('More', { exact: true }).click();
    await page.getByRole('link', { name: 'Pairing and desktop mode', exact: true }).click();
    await expect(page).toHaveURL(base + 'counter#launch');
    await expect(page.locator('.section-menu')).not.toHaveAttribute('open', '');
    await page.getByRole('link', { name: 'Game Launcher', exact: true }).click();
    await expect(page).toHaveURL(base);
    await expect(sections()).toHaveCount(0);
    await page.goBack();
    await expect(page).toHaveURL(base + 'counter#launch');
    await expect(selected('Launch settings')).toHaveAttribute('aria-current', 'location');
    await page.setViewportSize({ width: 1280, height: 900 });
}
async function pair(page) {
    await page.getByRole('button', { name: 'Forget this browser pairing' }).click();
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.pairing-code')).toHaveText(/^\d{6}$/);
    await page.getByRole('button', { name: 'Cancel pairing', exact: true }).click();
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Not paired · disconnected');
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.pairing-code')).toHaveText(/^\d{6}$/);
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Pairing · waiting for confirmation');
    await expect(page.getByRole('button', { name: 'The codes match' })).toBeFocused();
    await page.getByRole('button', { name: 'The codes match' }).click();
    await expect(page.locator('.launch-status')).toHaveText('Paired. Enter your game settings.');
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Paired · connected');
    await expect(page.locator('.desktop-state')).toContainText('GUI');
}
export async function secureLaunch(page, base) {
    await page.goto(base + 'counter');
    await openDesktop(page);
    await page.getByLabel('Desktop launcher mode', { exact: true }).selectOption('console');
    await pair(page);
    await expect(page.locator('.desktop-state')).toContainText('The existing host is still running');
    await page.getByLabel('Desktop launcher mode', { exact: true }).selectOption('gui');
    await gameSettings(page);
    const consoles = []; page.on('console', message => consoles.push(message.text()));
    await page.getByRole('button', { name: 'Browse on desktop', exact: true }).click();
    await expect(page.getByLabel('Installation folder')).toHaveValue('C:\\Games\\日本語');
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
    await desktopSettings(page);
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Pairing saved · disconnected');
    expect(page.url()).not.toContain('synthetic');
    expect(consoles.join('\n')).not.toContain('synthetic-secret');
    expect(await page.evaluate(() => JSON.stringify(localStorage))).not.toContain('synthetic-secret');
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.launch-status')).toHaveText('Connected. Enter your game settings.');
    await gameSettings(page);
    await page.getByLabel('Password', { exact: true }).fill('synthetic-cancel');
    await page.getByRole('button', { name: 'Launch game', exact: true }).click();
    await page.getByRole('button', { name: 'Cancel launch' }).click();
    await expect(page.locator('.launch-status')).toHaveText('Launch cancelled.', { timeout: 15000 });
}
export async function unavailableHandler(page, base) {
    await page.goto(base + 'spellborn');
    await openDesktop(page);
    await page.route('http://127.0.0.1:47832/**', route => route.abort());
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.launch-status')).toContainText('Cannot connect to WebLaunch');
    await gameSettings(page);
    await expect(page.getByRole('button', { name: 'Launch game', exact: true })).toBeDisabled();
    await desktopSettings(page);
    await page.getByRole('button', { name: 'Forget this browser pairing' }).click();
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Not paired · disconnected');
    await page.unroute('http://127.0.0.1:47832/**');
}

export async function settingsDialogs(page, base) {
    await page.goto(base + 'counter');
    await expect(page.getByRole('dialog')).not.toBeVisible();
    const gear = page.getByRole('button', { name: 'Game settings', exact: true });
    await gear.click();
    const dialog = page.getByRole('dialog');
    await expect(dialog).toHaveAccessibleName('Login settings');
    await expect(page.getByLabel('Installation folder')).toBeFocused();
    await page.getByLabel('Password', { exact: true }).fill('synthetic-dismiss-secret');
    await page.getByLabel('One-time password (optional)').fill('123456');
    // Native modal focus containment keeps background navigation outside the tab order.
    await dialog.getByRole('button', { name: 'Close settings', exact: true }).focus();
    for (let i = 0; i < 18; i++) {
        await page.keyboard.press('Tab');
        expect(await dialog.evaluate(el => el.contains(document.activeElement) || document.activeElement === document.body)).toBe(true);
    }
    await page.keyboard.press('Escape');
    await expect(dialog).not.toBeVisible();
    await expect(gear).toBeFocused();
    await page.getByRole('button', { name: 'Play now', exact: true }).click();
    await expect(page.getByLabel('Password', { exact: true })).toHaveValue('');
    await expect(page.getByLabel('One-time password (optional)')).toHaveValue('');
    await dialog.getByRole('button', { name: 'Cancel', exact: true }).click();
    await expect(dialog).not.toBeVisible();
    await page.setViewportSize({ width: 390, height: 844 });
    await page.getByRole('button', { name: 'Play now', exact: true }).click();
    expect(await dialog.evaluate(el => el.getBoundingClientRect().width < innerWidth && el.getBoundingClientRect().height < innerHeight)).toBe(true);
    await page.keyboard.press('Escape');
    await page.goto(base + 'spellborn');
    await page.getByRole('button', { name: 'Play now', exact: true }).click();
    await expect(dialog).toHaveAccessibleName('Game settings');
    await expect(page.getByLabel('Installation folder')).toBeVisible();
    await expect(page.getByLabel('Password', { exact: true })).toHaveCount(0);
    await page.keyboard.press('Escape');
    await page.setViewportSize({ width: 1280, height: 900 });
}

export async function browserRecovery(page, base) {
    const diagnostics = [];
    const observe = message => diagnostics.push(message.text());
    page.on('console', observe);
    try {
        await page.goto(base + 'counter');
        await page.getByRole('button', { name: 'Play now', exact: true }).click();
        const alignment = await page.locator('.path-control').evaluate(el => {
            const input = el.querySelector('input').getBoundingClientRect();
            const button = el.querySelector('button').getBoundingClientRect();
            return Math.abs(input.top - button.top) < 1 && Math.abs(input.height - button.height) < 1;
        });
        expect(alignment).toBe(true);
        await page.keyboard.press('Escape');
        await page.evaluate(() => {
            window.dispatchEvent(new ErrorEvent('error', { cancelable: true, message: 'synthetic-private-path-password', error: new Error('synthetic-private-path-password') }));
            window.dispatchEvent(new PromiseRejectionEvent('unhandledrejection', { cancelable: true, promise: Promise.resolve(), reason: 'synthetic-otp-secret' }));
        });
        await expect(page.locator('#browser-notice')).toBeVisible();
        expect(diagnostics.some(line => line.includes('BROWSER_SCRIPT_ERROR'))).toBe(true);
        expect(diagnostics.some(line => line.includes('BROWSER_ASYNC_ERROR'))).toBe(true);
        expect(diagnostics.join('\n')).not.toContain('synthetic-private-path-password');
        expect(diagnostics.join('\n')).not.toContain('synthetic-otp-secret');
        await page.route('**/js/dialog.js', route => route.abort());
        await page.reload();
        await page.getByRole('button', { name: 'Play now', exact: true }).click();
        await expect(page.locator('.launch-summary')).toContainText('Could not open settings');
        await expect(page.locator('#blazor-error-ui')).not.toBeVisible();
        expect(diagnostics.some(line => line.includes('open-settings failed'))).toBe(true);
        await page.unroute('**/js/dialog.js');
        await page.reload();
        await page.getByRole('button', { name: 'Play now', exact: true }).click();
        await expect(page.getByRole('dialog')).toBeVisible();
        await page.keyboard.press('Escape');
        await page.route('**/js/navigation.js', route => route.abort());
        await page.reload();
        await expect(page.getByRole('heading', { name: 'Let’s reload the launcher' })).toBeVisible();
        expect(diagnostics.some(line => line.includes('render failed'))).toBe(true);
        await page.unroute('**/js/navigation.js');
        await page.getByRole('button', { name: 'Reload launcher', exact: true }).click();
        await expect(page.getByRole('heading', { name: 'Final Fantasy XIV', exact: true })).toBeVisible();
    } finally { page.off('console', observe); }
}
