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
dotnet workload install wasm-tools
dotnet restore handlerlaunch.sln -p:EnableWindowsTargeting=true
dotnet build handlerlaunch.sln -c Release -p:EnableWindowsTargeting=true
dotnet test WebLaunch.Tests/WebLaunch.Tests.csproj -c Release
npm ci --prefix LaunchApp
dotnet run --project LaunchApp --urls http://localhost:5148
```

On Windows, set `WEBLAUNCH_DEVELOPMENT_ORIGINS=http://localhost:5148` only for the
local desktop process. Build packaged plugins with `scripts/package.ps1` before
running `artifacts/desktop/WMconsole.exe`. Merely building the solution does not
install/register the handler or copy plugins into arbitrary directories.

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
