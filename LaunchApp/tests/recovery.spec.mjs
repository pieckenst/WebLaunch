import { test, expect } from '@playwright/test';
import { prepare } from './scenarios.mjs';

const base = process.env.WEBLAUNCH_TEST_URL || 'http://localhost:5148/WebLaunch/';

test('a repeatedly failing game page can force-load the library', async ({ page }) => {
    await page.route('**/js/navigation.js', async route => {
        const response = await route.fetch();
        const body = (await response.text()).replace('export function sync(route, selected, dotnet) {',
            "export function sync(route, selected, dotnet) { if (route) throw new Error('Synthetic game page failure');");
        await route.fulfill({ response, body });
    });
    await prepare(page, base);
    await page.goto(base + 'counter');
    await expect(page.locator('.app-recovery')).toBeVisible();
    await page.getByRole('button', { name: 'Reload launcher', exact: true }).click();
    await expect(page.locator('.app-recovery')).toBeVisible();
    await page.evaluate(() => { window.documentBeforeRecovery = true; });
    const link = page.getByRole('link', { name: 'Return to game library' });
    await expect(link).toHaveAttribute('href', base);
    await link.click();
    await expect(page).toHaveURL(base);
    await expect(page.getByRole('heading', { name: 'Your game library' })).toBeVisible();
    expect(await page.evaluate(() => window.documentBeforeRecovery)).toBeUndefined();
});

for (const action of ['cancel', 'close', 'timeout']) {
    test(`pending confirmation supports ${action} and ignores late success`, async ({ page }) => {
        // Stall only the interop promise to exercise the UI's own cancellation deadline.
        await page.route('**/js/bridge.js', async route => {
            const response = await route.fetch();
            let body = (await response.text()).replace('export async function confirmPairing() {', 'async function originalConfirmPairing() {');
            body = body.replace('export async function disconnect() {', 'export async function disconnect() { window.pairingDisconnected = true;');
            body += '\nexport function confirmPairing() { return new Promise(resolve => { window.finishPairing = resolve; }); }';
            await route.fulfill({ response, body });
        });
        await prepare(page, base);
        await page.goto(base + 'counter');
        if (action === 'timeout') await page.clock.install();
        await page.locator('.connection-summary').click();
        await page.getByRole('button', { name: 'Connect', exact: true }).click();
        await expect(page.locator('.pairing-code')).toHaveText(/^\d{6}$/);
        await page.getByRole('button', { name: 'The codes match' }).click();
        await expect.poll(() => page.evaluate(() => typeof window.finishPairing)).toBe('function');
        await expect(page.getByRole('button', { name: 'Cancel pairing', exact: true })).toBeEnabled();
        await expect(page.getByRole('button', { name: 'Close settings', exact: true })).toBeEnabled();
        if (action === 'cancel') await page.getByRole('button', { name: 'Cancel pairing', exact: true }).click();
        else if (action === 'close') await page.keyboard.press('Escape');
        else await page.clock.fastForward(120_100);
        await expect.poll(() => page.evaluate(() => window.pairingDisconnected)).toBe(true);
        if (action === 'close') {
            await expect(page.getByRole('dialog')).not.toBeVisible();
            await page.locator('.connection-summary').click();
        }
        await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Not paired · disconnected');
        await expect(page.locator('.pairing-code')).toHaveCount(0);
        await expect(page.getByRole('button', { name: 'Connect', exact: true })).toBeEnabled();
        await expect(page.locator('.launch-status')).toContainText(action === 'timeout' ? 'Pairing expired' : 'Pairing cancelled');
        await page.evaluate(() => window.finishPairing({ paired: true, desktopMode: 'gui', canBrowseFolders: true }));
        await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Not paired · disconnected');
        await page.getByRole('button', { name: 'Close settings', exact: true }).click();
        await expect(page.getByRole('dialog')).not.toBeVisible();
    });
}

test('disconnect aborts the bridge request without saving late pairing approval', async ({ page }) => {
    await prepare(page, base);
    const result = await page.evaluate(async () => {
        const bridge = await import('./js/bridge.js');
        await bridge.connect();
        const originalFetch = window.fetch;
        let requestStarted;
        const started = new Promise(resolve => { requestStarted = resolve; });
        let aborted = false;
        window.fetch = async (url, options) => {
            if (String(url).includes('/v2/session/')) {
                window.fetch = originalFetch;
                // The host has handled the envelope, but its response is still pending in the browser.
                await originalFetch(url, options);
                return new Promise((resolve, reject) => {
                    options.signal.addEventListener('abort', () => {
                        aborted = true;
                        reject(new DOMException('Aborted', 'AbortError'));
                    }, { once: true });
                    requestStarted();
                });
            }
            return originalFetch(url, options);
        };
        const confirmation = bridge.confirmPairing().then(() => 'paired', () => 'cancelled');
        await started;
        try { await bridge.disconnect(); } catch { /* The cancelled response leaves a gap in received sequence numbers. */ }
        const outcome = await confirmation;
        const saved = await bridge.pairingSaved();
        // Native approval may already be saved; forget the incomplete browser identity before re-pairing.
        await bridge.forget();
        const fresh = await bridge.connect();
        await bridge.disconnect();
        return { aborted, outcome, saved, freshCode: fresh.code };
    });
    expect(result).toEqual({ aborted: true, outcome: 'cancelled', saved: false, freshCode: expect.stringMatching(/^\d{6}$/) });
});
