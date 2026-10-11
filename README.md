# WebLaunch

A static browser UI and Windows desktop launcher for Final Fantasy XIV and
Chronicles of Spellborn.

## Install and connect

1. Download and extract the complete Windows bundle from [Releases](https://github.com/pieckenst/WebLaunch/releases/).
2. Run `WMconsole.exe` and choose **Install / repair browser launch link** once.
3. Open [WebLaunch](https://pieckenst.github.io/WebLaunch/) in Chrome or Edge on Windows.
4. Choose a game, **Open desktop launcher**, then **Connect**. Allow local network
   access if prompted. Compare the pairing code and confirm in both windows.
5. Enter the installation folder and, for FFXIV, login details. OTP is optional.
   Keep the desktop window open while launching and while its external addons run.

The website never places credentials in launch URLs. Legacy URL support requires
explicit desktop opt-in, expires after 24 hours, and retains its documented
credential-exposure risk. A changed desktop identity requires explicit re-pairing.
Use the native **Forget all paired browsers** control to revoke access.

## Console and unattended use

The executable remains a console application. `WMconsole.exe --console` runs the
same paired bridge and plugin services without a WPF window. Initial pairing prints
the six-digit code and requires typing the matching browser code in the terminal.
Press Ctrl+C to shut down the host and its addon monitoring cleanly.

Use `WMconsole.exe --console --quiet` for an unattended host with no application
output. It accepts previously paired browsers and declines all new pairing attempts.
Pair interactively first. The calling terminal/process controls console-window
visibility (for example, PowerShell `Start-Process -WindowStyle Hidden`).
`WMconsole.exe --console --install` registers/repairs the protocol and exits.
All modes use the same per-user instance, DPAPI trust store and protocol pipe.
Legacy opt-in still requires the graphical desktop settings and expires after 24 hours.

## Development

Requirements: .NET SDK **10.0.401**, `wasm-tools`, Node 24, npm, and Windows for
WPF/Win32 execution. Linux can build with Windows targeting enabled.

```sh
npm ci --prefix LaunchApp
dotnet workload install wasm-tools
dotnet restore handlerlaunch.sln -p:EnableWindowsTargeting=true
dotnet build handlerlaunch.sln -c Release -p:EnableWindowsTargeting=true
dotnet test WebLaunch.Tests/WebLaunch.Tests.csproj -c Release
dotnet run --project LaunchApp --urls http://localhost:5148
```

On Windows, set `WEBLAUNCH_DEVELOPMENT_ORIGINS=http://localhost:5148` only for the
local desktop process. Build packaged plugins with `scripts/package.ps1` before
running `artifacts/desktop/WMconsole.exe`. Merely building the solution does not
install/register the handler or copy plugins into arbitrary directories.

LaunchApp uses LumexUI 2.4.0 alongside MasaBlazor. Its build invokes the pinned
Tailwind v4 CLI through `npm run build:css`. Package theme inputs are resolved from
NuGet restore metadata and staged under `obj`; generated CSS stays out of Git.
For builds without Node/npm dependencies, pass `-p:SkipLumexStyles=true` to skip
CSS generation (browser styling requires previously generated CSS).
The Tailwind 4.3.0 lockfile uses Parcel watcher 2.6.0, avoiding the vulnerable
watcher dependency pinned by CLI 4.3.3. Both themes use Lumex CSS tokens, synchronized
with Masa and the namespaced WebLaunch preference.

In the browser, select **GUI** or **Console** before choosing **Open desktop launcher**.
The credential-free bootstrap carries that mode. An already-running per-user host
keeps its mode; the paired connection reports its actual mode and explains how to
switch. Pairing status distinguishes pending, connected, and saved-but-disconnected
states. Quiet console remains a desktop-only option.

## Production-path browser checks

```sh
bash scripts/publish-web.sh
python3 scripts/serve-preview.py
# Separate terminal: test-only host, synthetic identities only; never launches games.
dotnet run --project WebLaunch.SmokeHost -c Release
# Separate terminal:
npm exec --prefix LaunchApp -- playwright install chromium
npm test --prefix LaunchApp
```

The preview serves `/WebLaunch/`. In CodeRabbit's shared Preview, use
`WEBLAUNCH_TEST_URL=http://localhost:5148/WebLaunch/ npm run test:shared --prefix LaunchApp`.
Do not run tests that bind port 47832 concurrently with the synthetic or real host.

## Packaging and release

Run `scripts/package.ps1` on Windows. The resulting self-contained
`artifacts/WebLaunch-win-x64.zip` contains both plugins, native dependencies and
notices; no generated dependency folders belong in Git. CI validates Linux and
Windows builds and uploads the bundle and website artifacts. GitHub Pages deploys
through the manually dispatched workflow on `main` after handler rollout.

Before release, complete [Windows acceptance checks](docs/windows-acceptance.md).
See [architecture](docs/architecture.md), [protocol/security](docs/protocol-v2.md),
and [third-party components](docs/THIRD-PARTY.md).

## Windows progress notifications and diagnostics

Run **Install / repair browser launch link** again after upgrading (or
`WMconsole.exe --console --install`). This explicitly creates the Start-menu
shortcut/application identity needed for Windows 10/11 notifications. Both GUI and
console launches report preparing, sign-in, updating and starting phases, followed
by success, failure or cancellation. Progress is indeterminate because plugins do
not report reliable percentages. Open Windows Notification Center to check it;
quiet mode does not show popup banners. Windows notification settings / Do Not
Disturb can suppress banners. Notification failures are logged and never stop a
launch. Clicking a notification opens the existing handler through its v2 bootstrap.

Start `WMconsole.exe --debug` for GUI diagnostics or
`WMconsole.exe --console --debug` for console diagnostics. Stop any running host
first; starting a second instance does not change the first host's debug setting.
Logs live in `%LOCALAPPDATA%\WebLaunch\Logs\desktop.log`, with one rotated backup
at 1 MiB. Normal mode records host, launch and plugin warning/error events; debug
adds plugin debug/information and successful notification events. Fields contain
controlled event names, request IDs, phases and bundled-plugin identifiers. Raw
plugin messages, passwords, OTPs, session tokens, paths and launch URLs are excluded.

The browser's **Browse on desktop** button uses a paired handler to select and
return an absolute Windows folder. Browser directory handles cannot supply that
path reliably. Manual entry remains available for older handlers. Both sides
validate the path before launch, and credentials continue over the encrypted bridge.
Browser console diagnostics contain fixed operation/error codes; recoverable errors
stay in the UI, and render/state failures offer a reload action.
