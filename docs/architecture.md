# Architecture and compatibility

## Boundaries

- `LaunchApp`: static Blazor UI. No account data is stored in browser storage or URLs.
- `WebLaunch.Core`: launch DTOs, legacy parser, channel cryptography, path containment,
  staged archive installation, and the Spellborn update service.
- `WebLaunch.Bridge`: loopback HTTP transport, trust/pairing, session expiry, launch
  dispatch and structured status. Depends on injected trust, prompt and launch interfaces.
- `handlerlaunch`: Windows per-user process, WPF pairing/settings, DPAPI storage,
  explicit protocol registration and game orchestration. Graphical and console modes
  share `DesktopRuntime`; quiet console mode permits existing pairings only.
- `El'GarnanPluginSystem`: trusted in-process application plugins and optional renderer.
- `CoreLibLaunchSupport` / `LibDalamud`: FFXIV authentication, native game process,
  Dalamud bootstrap and patch support. `DalamudGameSession` owns `AddonManager` and
  an independent process handle, starts configured enabled addons, observes game
  exit, and stops addons then. Launch returns after startup, without waiting for
  exit. Closing WebLaunch also stops its addon sessions; it never kills the game.

## Existing integration APIs

`IGamePlugin`, the base plugin constructor, existing public launch models, plugin
IDs and `Elgarnan` assembly name/version are retained. Bundled plugins implement
an optional `ICancellableGamePlugin` extension. Old plugins receive their original
in-process credential dictionary and boolean launch contract. Their code remains
trusted and can access process memory; an assembly load context is not a sandbox.
Cancellation of an old plugin cannot forcibly interrupt its implementation.

`LibLaunchSupport` and handler support entry points delegate to the maintained
services. .NET 10 is now required for host/library consumers. The automated legacy
fixture verifies the original source-level plugin interface, loading and lifecycle;
arbitrary third-party binary compatibility requires testing those actual binaries.

Plugin bundles have `plugin.json` containing `id`, `assembly` and manifest `version: 1`.
Legacy DLL discovery remains available. Dependencies are resolved in collectible,
shadow-copied contexts, with contract, rendering and Dalamud/core assemblies shared
with the default context. Automatic reload is disabled for normal desktop startup.

## Updates and recovery

All remote relative paths are resolved beneath their selected root. Windows device
names, traversal, UNC/device paths, alternate streams and existing reparse points
are rejected. This guards untrusted downloaded names; it cannot protect against a
privileged local process replacing directories concurrently.

ZIP files are prevalidated and extracted into `.weblaunch-transaction/stage`.
Before replacing each target, the flushed recovery journal records its previous
state; originals move to the backup directory. Cancellation or failure rolls back
replacements. An interrupted installation is recovered before another update.
The filesystem commit precedes version metadata: a crash between them causes a
safe reinstallation, rather than claiming an incomplete version is installed.
Recovery metadata/backups are retained if rollback fails. A journal-less staging
directory requires inspection; the installer does not guess what to delete.

Provider MD5/SHA1 fields remain compatibility integrity checks, not signatures.
TLS certificate verification is enabled. Downloads and asynchronous updater calls
have bounded retries/cancellation. FFXIV patch binary algorithms remain unchanged;
containment is applied at their filesystem boundary.

## Browser sections and component libraries

MasaBlazor supplies page content; LumexUI v2 supplies navigation, links and controls.
`GamePageBase` normalizes old route/query section links for both games to base-aware
fragments. `GameNavigation` owns the route and selected section, including refresh
and Back/Forward. A scoped JS adapter reports hash-only anchor/history changes
that Blazor does not surface through its managed location event. It clears the subheader when leaving a game; it never redirects
section selection to the library.

`SectionTabBar` wraps Lumex navigation with `Sections`, `ActiveSection`, `Actions`
and `Menu` slots. Its links use `LumexComponent` anchors with fragment-specific
`aria-current="location"`; Lumex's ordinary page links ignore fragments when matching.
The sections remain visible in the document, so these are navigation links rather
than ARIA tab panels. The URL is the selection source. Manual scrolling does not
rewrite history or change the selected link. The overflow strip stays usable on
mobile, menus support Escape, and measured sticky heights account for wrapping.

Example extension:

```razor
<SectionTabBar Route="counter" Sections="sections" ActiveSection="selected">
    <Actions><LumexButton OnClick="RefreshStatus">Refresh status</LumexButton></Actions>
    <Menu><LumexLink Href="counter#launch">Launch settings</LumexLink></Menu>
</SectionTabBar>
```

### Game settings dialogs

The game header keeps Play Now, a settings gear and live pairing state together.
Both launch controls open the same settings dialog; the pairing summary opens its
Desktop connection view. Game settings and connection setup share one controller,
so changing views does not create another bridge session. GUI/console selection,
pairing confirmation and recovery remain available in Desktop connection.

The native HTML dialog supplies an inert backdrop, focus containment and focus
restoration. Escape, Cancel and Close clear password/OTP inputs; closing during a
launch requests cancellation, and closing a pending pairing disconnects it. Initial
focus goes to the installation folder or desktop-mode selector. The dialog scrolls
within the viewport on mobile and at browser zoom. Icons are inline SVGs, avoiding
an external font dependency. Lumex v2 owns navigation/theme controls; Masa remains
in use for page content and the application theme.
