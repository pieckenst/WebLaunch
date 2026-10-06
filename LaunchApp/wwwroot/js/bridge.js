const endpoint = 'http://127.0.0.1:47832';
const encoder = new TextEncoder();
const decoder = new TextDecoder();
const b64 = bytes => btoa(String.fromCharCode(...new Uint8Array(bytes)));
const unb64 = text => Uint8Array.from(atob(text), c => c.charCodeAt(0));
const digest = text => crypto.subtle.digest('SHA-256', encoder.encode(text));
let channel;
let pending = Promise.resolve();

async function database() {
    return new Promise((resolve, reject) => {
        const request = indexedDB.open('weblaunch.pairing.v2', 1);
        request.onupgradeneeded = () => request.result.createObjectStore('identity');
        request.onsuccess = () => resolve(request.result);
        request.onerror = () => reject(new Error('Browser pairing storage is unavailable.'));
    });
}
async function readIdentity() {
    const db = await database();
    try {
        return await new Promise((resolve, reject) => {
            const request = db.transaction('identity').objectStore('identity').get('current');
            request.onsuccess = () => resolve(request.result);
            request.onerror = () => reject(new Error('Unable to read browser pairing.'));
        });
    } finally { db.close(); }
}
async function saveIdentity(identity) {
    const db = await database();
    try {
        await new Promise((resolve, reject) => {
            const transaction = db.transaction('identity', 'readwrite');
            transaction.objectStore('identity').put(identity, 'current');
            transaction.oncomplete = resolve;
            transaction.onerror = () => reject(new Error('Unable to save browser pairing.'));
            transaction.onabort = () => reject(new Error('Unable to save browser pairing.'));
        });
    } finally { db.close(); }
}
async function post(path, body, timeout = 15000) {
    let response;
    try {
        response = await fetch(endpoint + path, {
            method: 'POST', headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify(body), credentials: 'omit', cache: 'no-store',
            redirect: 'error', signal: AbortSignal.timeout(timeout)
        });
    } catch {
        throw new Error('Cannot connect to WebLaunch. Open or update the desktop launcher, allow local network access, then retry.');
    }
    if (!response.ok) {
        if (response.status === 410) throw new Error('Connection expired. Connect again.');
        if (response.status === 429) throw new Error('Pairing is busy or temporarily locked. Close other pairing requests and retry in five minutes.');
        if (response.status === 403) throw new Error('Pairing was declined or expired. Connect again and compare both codes.');
        throw new Error('The desktop launcher rejected this request. Update the launcher and reconnect.');
    }
    return response.json();
}
export async function connect() {
    channel = undefined;
    if (!crypto.subtle || !globalThis.indexedDB) throw new Error('Secure browser storage and HTTPS are required.');
    let identity = await readIdentity();
    if (!identity) {
        const keys = await crypto.subtle.generateKey({ name: 'ECDSA', namedCurve: 'P-256' }, false, ['sign', 'verify']);
        identity = { privateKey: keys.privateKey, publicKey: keys.publicKey, desktop: null };
        await saveIdentity(identity);
    }
    const ephemeral = await crypto.subtle.generateKey({ name: 'ECDH', namedCurve: 'P-256' }, false, ['deriveBits']);
    const hello = {
        version: 2,
        identity: b64(await crypto.subtle.exportKey('spki', identity.publicKey)),
        ephemeral: b64(await crypto.subtle.exportKey('spki', ephemeral.publicKey)),
        nonce: b64(crypto.getRandomValues(new Uint8Array(32)))
    };
    const clientTranscript = `weblaunch-v2\n${location.origin}\n${hello.identity}\n${hello.ephemeral}\n${hello.nonce}`;
    hello.signature = b64(await crypto.subtle.sign({ name: 'ECDSA', hash: 'SHA-256' }, identity.privateKey, encoder.encode(clientTranscript)));
    const response = await post('/v2/hello', hello);
    if (!/^[a-f0-9]{32}$/.test(response.sessionId) || typeof response.identity !== 'string' || response.identity.length > 256 ||
        typeof response.ephemeral !== 'string' || response.ephemeral.length > 256 || unb64(response.nonce).length !== 32)
        throw new Error('Invalid desktop handshake.');
    if (identity.desktop && identity.desktop !== response.identity)
        throw new Error('The desktop identity changed. Verify your installation, then forget this browser pairing and pair again.');
    if (response.knownBrowser && !identity.desktop)
        throw new Error('Pairing state changed. Forget this browser pairing and pair again.');
    const transcript = `${clientTranscript}\n${response.identity}\n${response.ephemeral}\n${response.nonce}\n${response.sessionId}`;
    const desktopKey = await crypto.subtle.importKey('spki', unb64(response.identity), { name: 'ECDSA', namedCurve: 'P-256' }, false, ['verify']);
    if (!await crypto.subtle.verify({ name: 'ECDSA', hash: 'SHA-256' }, desktopKey, unb64(response.signature), encoder.encode(transcript)))
        throw new Error('Desktop signature verification failed.');
    const peer = await crypto.subtle.importKey('spki', unb64(response.ephemeral), { name: 'ECDH', namedCurve: 'P-256' }, false, []);
    const secret = new Uint8Array(await crypto.subtle.deriveBits({ name: 'ECDH', public: peer }, ephemeral.privateKey, 256));
    const salt = new Uint8Array(await digest(transcript));
    const hkdf = await crypto.subtle.importKey('raw', secret, 'HKDF', false, ['deriveKey']);
    secret.fill(0);
    const derive = direction => crypto.subtle.deriveKey({ name: 'HKDF', hash: 'SHA-256', salt, info: encoder.encode('weblaunch-v2/' + direction) }, hkdf, { name: 'AES-GCM', length: 256 }, false, ['encrypt', 'decrypt']);
    const code = (new DataView(salt.buffer).getUint32(0) % 1000000).toString().padStart(6, '0');
    channel = { id: response.sessionId, identity, desktop: response.identity, code, hash: b64(salt), send: await derive('client'), receive: await derive('desktop'), sent: 0, received: 0, paired: false };
    if (response.knownBrowser) await confirmPairing();
    return { paired: channel.paired, code: channel.paired ? '' : code, desktopMode: channel.desktopMode || '', canBrowseFolders: !!channel.canBrowseFolders };
}
function nonce(sequence) {
    const bytes = new Uint8Array(12);
    new DataView(bytes.buffer).setBigUint64(4, BigInt(sequence));
    return bytes;
}
async function exchange(command) {
    const current = channel;
    if (!current) throw new Error('Connect to the desktop launcher first.');
    const sequence = ++current.sent;
    const plaintext = encoder.encode(JSON.stringify(command));
    let encrypted;
    try {
        encrypted = await crypto.subtle.encrypt({ name: 'AES-GCM', iv: nonce(sequence), additionalData: encoder.encode(`${current.hash}\nclient\n${sequence}`), tagLength: 128 }, current.send, plaintext);
    } finally { plaintext.fill(0); }
    try {
        const response = await post('/v2/session/' + current.id, { sequence, ciphertext: b64(encrypted) }, ['confirm', 'browse'].includes(command.action) ? 120000 : 15000);
        if (response.sequence !== current.received + 1 || typeof response.ciphertext !== 'string' || response.ciphertext.length > 60000)
            throw new Error('Invalid desktop response.');
        const bytes = new Uint8Array(await crypto.subtle.decrypt({ name: 'AES-GCM', iv: nonce(response.sequence), additionalData: encoder.encode(`${current.hash}\ndesktop\n${response.sequence}`), tagLength: 128 }, current.receive, unb64(response.ciphertext)));
        try { current.received = response.sequence; return JSON.parse(decoder.decode(bytes)); }
        finally { bytes.fill(0); }
    } catch (error) { channel = undefined; throw error; }
}
function send(command) {
    const result = pending.then(() => exchange(command));
    pending = result.catch(() => {});
    return result;
}
export async function confirmPairing() {
    const current = channel;
    if (!current) throw new Error('Connect again to pair.');
    const reply = await send({ action: 'confirm', code: current.code });
    if (!reply.paired) throw new Error('Pairing was not confirmed.');
    current.identity.desktop = current.desktop;
    await saveIdentity(current.identity);
    current.paired = true;
    current.desktopMode = reply.desktopMode || 'unknown';
    current.canBrowseFolders = !!reply.canBrowseFolders;
    return reply;
}
export async function launch(request) {
    if (!channel?.paired) throw new Error('Complete pairing before signing in.');
    try { return await send({ action: 'launch', launch: request }); }
    finally { request.password = ''; request.otp = ''; }
}
export async function browseFolder() {
    if (!channel?.paired) throw new Error('Pair the desktop launcher before choosing a folder.');
    return send({ action: 'browse' });
}
export const status = () => send({ action: 'status' });
export const cancel = () => channel?.paired ? send({ action: 'cancel' }) : Promise.resolve();
export async function disconnect() {
    try { if (channel) await send({ action: 'disconnect' }); } finally { channel = undefined; }
}
export async function forget() {
    try { await disconnect(); } catch { /* Local revocation must work while the desktop is unavailable. */ }
    const db = await database();
    try {
        await new Promise((resolve, reject) => {
            const tx = db.transaction('identity', 'readwrite');
            tx.objectStore('identity').delete('current');
            tx.oncomplete = resolve; tx.onerror = reject;
        });
    } finally { db.close(); }
}
export function supported() {
    return /Windows/i.test(navigator.userAgent) && /(?:Chrome|Edg)\//.test(navigator.userAgent) && !!crypto.subtle;
}
export function preference(key, value) {
    const name = 'weblaunch.' + key;
    try { if (value !== undefined) localStorage.setItem(name, value); return localStorage.getItem(name); }
    catch { return null; }
}
export function applyTheme(value) {
    const dark = value === 'dark' || (value !== 'light' && matchMedia('(prefers-color-scheme: dark)').matches);
    document.documentElement.dataset.theme = dark ? 'dark' : 'light';
    document.documentElement.classList.toggle('dark', dark);
    document.documentElement.classList.toggle('light', !dark);
    return dark;
}
export function scrollSection(id) { document.getElementById(id)?.scrollIntoView({ behavior: matchMedia('(prefers-reduced-motion: reduce)').matches ? 'instant' : 'smooth' }); }

export function focus(id) { document.getElementById(id)?.focus(); }

export async function pairingSaved() { return !!(await readIdentity())?.desktop; }
export function bootstrapLink(mode) {
    if (mode !== 'gui' && mode !== 'console') throw new Error('Choose GUI or console mode.');
    return 'HandleWebRequest:connect?v=2&mode=' + mode;
}
