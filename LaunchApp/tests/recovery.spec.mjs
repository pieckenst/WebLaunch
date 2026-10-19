import { test, expect } from '@playwright/test';
import { prepare, pendingLaunchStatus } from './scenarios.mjs';

const base = process.env.WEBLAUNCH_TEST_URL || 'http://localhost:5148/WebLaunch/';

test('status transport failure re-establishes a fresh authenticated desktop session', async ({ page }) => {
    await prepare(page, base);
    await page.goto(base + 'counter');
    await page.locator('.connection-summary').click();
    await page.getByRole('button', { name: 'Connect', exact: true }).click();
    await expect(page.locator('.pairing-code')).toHaveText(/^\d{6}$/);
    await page.getByRole('button', { name: 'The codes match', exact: true }).click();
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Paired · connected');

    // The real bridge intentionally invalidates a channel after any failed exchange. Abort the
    // next status request, then allow the recovery handshake and its confirmation to use the
    // actual browser crypto and synthetic desktop host instead of mocking status().
    let aborted = false;
    await page.route('http://127.0.0.1:47832/v2/session/**', async route => {
        if (!aborted) {
            aborted = true;
            await route.abort();
            return;
        }
        await route.continue();
    });

    await expect(page.locator('.launch-status')).toHaveText('The desktop connection is restored.', { timeout: 25_000 });
    await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Paired · connected');

    // The desktop settings dialog is open. Switch to the game view and verify that the
    // recovered session actually enables the launch action instead of querying a hidden control.
    await page.getByRole('dialog').getByRole('button', { name: 'Game settings', exact: true }).click();
    await expect(page.getByRole('dialog')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Launch game', exact: true })).toBeEnabled();
    expect(aborted).toBe(true);
    await page.unroute('http://127.0.0.1:47832/v2/session/**');
});

test('manual reconnect wins over a status request from the replaced channel', async ({ page }) => {
    let releaseStatus = () => {};
    let statusRouteInstalled = false;
    let helloRouteInstalled = false;
    let heldStatus = false;
    let reconnectHandshakes = 0;
    let pairedSuccessfully = false;

    try {
        await prepare(page, base);
        await page.goto(base + 'counter');
        await page.locator('.connection-summary').click();
        await page.getByRole('button', { name: 'Connect', exact: true }).click();
        await expect(page.locator('.pairing-code')).toHaveText(/^\d{6}$/);
        await page.getByRole('button', { name: 'The codes match', exact: true }).click();
        await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Paired · connected');
        pairedSuccessfully = true;

        // Hold the next encrypted status exchange so a manual reconnect can replace its channel
        // while the old request is still in flight.
        const statusGate = new Promise(resolve => { releaseStatus = resolve; });
        await page.route('http://127.0.0.1:47832/v2/session/**', async route => {
            if (!heldStatus && route.request().method() === 'POST') {
                heldStatus = true;
                await statusGate;
                try { await route.abort(); } catch { /* The replaced JS channel may abort it first. */ }
                return;
            }
            await route.continue();
        });
        statusRouteInstalled = true;
        await page.route('http://127.0.0.1:47832/v2/hello', async route => {
            if (route.request().method() === 'POST') reconnectHandshakes++;
            await route.continue();
        });
        helloRouteInstalled = true;

        await expect.poll(() => heldStatus, { timeout: 15_000 }).toBe(true);
        const reconnect = page.getByRole('button', { name: 'Reconnect', exact: true });
        const reconnectClick = reconnect.click();
        await expect(reconnect).toBeDisabled();
        releaseStatus();
        await reconnectClick;

        await expect(page.locator('.launch-status')).toHaveText('Connected. Enter your game settings.');
        await expect(page.getByRole('status', { name: 'Pairing status' })).toHaveText('Paired · connected');
        // One fresh handshake is expected for the manual action. A competing automatic
        // recovery would issue another /v2/hello and could cancel the successful session.
        expect(reconnectHandshakes).toBe(1);
    } finally {
        releaseStatus();
        if (statusRouteInstalled) await page.unroute('http://127.0.0.1:47832/v2/session/**');
        if (helloRouteInstalled) await page.unroute('http://127.0.0.1:47832/v2/hello');
        // Only cancel explicitly when pairing did not complete. On success, let component
        // disposal disconnect the paired session; cancelling it here could race the monitor's
        // recovery loop and create a fresh pending session as the test ends.
        if (!pairedSuccessfully) {
            try {
                await page.evaluate(async () => {
                    const bridge = await import('./js/bridge.js');
                    try { await bridge.cancelPendingPairing(); }
                    finally { await bridge.forget(); }
                });
            } catch { /* The page may already have navigated or disposed. */ }
        }
    }
});

test('a failing navigation module can reload and return to the library', async ({ page }) => {
    await prepare(page, base);
    let failedModuleLoads = 0;
    await page.route('**/js/navigation.js', async route => {
        // Fail the initial game navigation and the explicit reload, then allow navigation
        // to the library. This avoids depending on whether Playwright's Page.url() reflects a
        // just-started full navigation before its module request reaches the route handler.
        if (failedModuleLoads < 2) {
            failedModuleLoads++;
            await route.abort();
            return;
        }
        await route.continue();
    });
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
            body = body.replace('export async function cancelPendingPairing() {', 'export async function cancelPendingPairing() { window.pairingDisconnected = true;');
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

for (const action of ['cancel', 'timeout', 'navigate']) {
    test(`pending launch status stops on ${action}`, async ({ page }) => {
        await pendingLaunchStatus(page, base, action);
    });
}

for (const state of ['cancelled', 'launched', 'failed']) {
    test(`nonterminal cancel keeps polling until ${state}`, async ({ page }) => {
        await pendingLaunchStatus(page, base, 'cancel-pending', state);
    });
}

test('nonterminal cancel retains the launch monitoring deadline', async ({ page }) => {
    await pendingLaunchStatus(page, base, 'cancel-pending-timeout');
});
