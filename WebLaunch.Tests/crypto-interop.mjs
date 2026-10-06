// Executes the production browser module against the real .NET bridge with synthetic data.
// Only IndexedDB is replaced; cryptography and HTTP use Node's Web Crypto and fetch implementations.
import { pathToFileURL } from 'node:url';
const state = new Map();
globalThis.location = { origin: 'https://pieckenst.github.io' };
globalThis.indexedDB = { open() {
    const request = {};
    queueMicrotask(() => { request.result = { close() {}, transaction() {
        const tx = { objectStore() { return {
            get(key) { const r = {}; queueMicrotask(() => { r.result = state.get(key); r.onsuccess(); }); return r; },
            put(value, key) { state.set(key, value); queueMicrotask(() => tx.oncomplete()); },
            delete(key) { state.delete(key); queueMicrotask(() => tx.oncomplete()); }
        }; } }; return tx;
    } }; request.onsuccess(); });
    return request;
} };
const originalFetch = globalThis.fetch;
globalThis.fetch = (url, options) => originalFetch(url, { ...options, headers: { ...options.headers, Origin: location.origin } });
const bridge = await import(pathToFileURL(process.argv[2]).href);
let connection = await bridge.connect();
if (connection.paired || !/^\d{6}$/.test(connection.code)) throw new Error('Expected initial pairing');
await bridge.confirmPairing();
const reply = await bridge.launch({ version: 2, requestId: crypto.randomUUID().replaceAll('-', ''), game: 'ffxiv', gamePath: 'C:\\Games\\日本語', username: 'synthetic-user', password: 'synthetic:+?=é🔐', otp: '', isSteam: false });
if (!reply.status.success) throw new Error('Launch did not succeed');
await bridge.disconnect();
connection = await bridge.connect();
if (!connection.paired) throw new Error('Remembered pairing did not reconnect');
await bridge.disconnect();
console.log('Production JS/.NET interop passed');
