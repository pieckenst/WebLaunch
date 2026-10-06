# Browser / desktop protocol v2

The static production origin is `https://pieckenst.github.io`. Origin trust covers
that whole origin, including other paths hosted by the same account. Treat all
scripts deployed there as trusted. For a stronger deployment isolation boundary,
use a dedicated origin and update the desktop allowlist before release.

`HandleWebRequest:connect?v=2` only opens/focuses the per-user Windows host. It
contains no credentials, bearer token or cryptographic secret. Desktop registration
quotes both executable and `%1`; browser requests never trigger installation.
The named pipe accepts current-user clients only and bounds input length and time.

## HTTP

The host binds only `127.0.0.1:47832`, checks exact Host/Origin, and accepts JSON POST
requests. OPTIONS is limited to allowlisted origins. Responses are not cached.
Development origins must be explicit loopback authorities in the desktop-only
`WEBLAUNCH_DEVELOPMENT_ORIGINS` setting (semicolon separated). Never accept arbitrary
origins from a launch URL. Chrome/Edge may require local network permission.

- `POST /v2/hello`: `{version:2, identity, ephemeral, nonce, signature}`.
- Response: `{sessionId, identity, ephemeral, nonce, signature, knownBrowser}`.
- `POST /v2/session/{sessionId}`: `{sequence, ciphertext}`; encrypted response uses
  the same envelope. Plaintext commands are `confirm`, `launch`, `status`, `cancel`.
- Launch payload fields: `version`, `requestId` (32 lowercase GUID hex digits),
  `game` (`ffxiv` or `spellborn`), `gamePath`, `username`, `password`, `otp`, `isSteam`.
- Results: `{paired, status:{state,message,completed,success}}`.
- 400: invalid message; 403: origin/pairing refusal; 410: expired/revoked session;
  415: unsupported media type/method; 429: admission or pairing cooldown.

## Cryptography and pairing

Signing identities and ephemeral agreement keys use P-256. Public keys are Base64
DER SPKI. Signatures are ECDSA/SHA256 with IEEE P1363 (r||s) encoding. Nonces are 32
random bytes, Base64. The client signs these newline-separated UTF-8 fields:

`weblaunch-v2`, Origin, client identity, client ephemeral key, client nonce.

The server transcript appends server identity, server ephemeral key, server nonce,
and session ID. The desktop signs this full transcript. The browser checks the
signature and pinned desktop identity before transmitting login information.

Both sides derive the raw ECDH secret, then HKDF-SHA256 with SHA256(transcript) as
salt. Directional info strings are `weblaunch-v2/client` and
`weblaunch-v2/desktop`; each yields a 32-byte AES-GCM key. The GCM nonce is four zero
bytes followed by the unsigned 64-bit big-endian sequence (starting at 1). AAD is
Base64(SHA256(transcript)), direction, and decimal sequence, separated by newlines.
Ciphertext is Base64(ciphertext || 16-byte tag). Sequence numbers are strictly
monotonic per direction; authenticated frames cannot be replayed or moved between
sessions/directions. A failed browser exchange discards the channel.

The pairing code is the first big-endian uint32 of SHA256(transcript), modulo one
million, zero-padded to six digits. The user must compare independent browser and
native displays and confirm both. No login submission is allowed before pairing.
Pairing expires after two minutes; three rejected confirmations impose a five-minute
cooldown. The host admits one new pairing at a time and at most 16 sessions.

Browser private signing keys are nonexportable Web Crypto keys in namespaced
IndexedDB; only the desktop public key is pinned alongside them. Windows protects
identity and trust data using DPAPI CurrentUser. Revoke paired browsers from the
native UI; browser Forget rotates its identity locally. Do not silently accept a
changed desktop identity. Credentials are transient; managed strings cannot be
reliably zeroed, so references are cleared promptly and never persisted/logged.

Sessions expire after ten idle minutes. Active jobs have a two-hour bound and
receive cancellation on session expiry/revocation/host shutdown. Each session
accepts one launch and the host remembers used request IDs for its lifetime,
bounded at 4096 (restart the host when exhausted). Message bodies are limited to
64 KiB. There is no remote relay and no HTTPS certificate bypass. The threat model
excludes malware already controlling the user's OS account or trusted web origin.

## Legacy migration

Old credential-bearing links are rejected unless the user explicitly enables
legacy mode in the desktop UI. Consent expires within 24 hours and warns that the
old URL encoding does not protect credentials from browser/OS argument exposure.
No updated website path falls back to legacy mode. Existing bookmarks remain.
Remove legacy mode in the next protocol-major release after integrators migrate.
Publish the handler before the site; old handlers cannot complete a v2 handshake
and the site shows installation/update guidance instead of sending credentials.

## Desktop presentation mode

The bootstrap accepts `HandleWebRequest:connect?v=2&mode=gui` or `mode=console`.
Omitting mode preserves GUI as the default. Duplicate/unknown fields, unsupported
versions and modes are rejected before starting a new host. The URL contains no
credentials, path or shell arguments. Quiet mode requires local CLI configuration.
A running host is not replaced during active work: encrypted replies contain
`desktopMode` (`gui`, `console`, or `quiet`) so the browser can show the actual mode
and ask the user to close the current host before switching.

Encrypted `disconnect` cancels pending pairing/launch work and expires the session.
The browser can cancel pairing and retry immediately without waiting for expiry.
