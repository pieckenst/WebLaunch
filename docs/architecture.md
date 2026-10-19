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
A manifest defines one bundle, whose subdirectories are shadow-copied recursively so
private managed assemblies, native runtimes and assets remain alongside the plugin.
Legacy DLL discovery remains available without requiring every installed plugin to
add a manifest: root-level files and recognized private asset/runtime trees are
preserved, while a nested directory containing another plugin manifest or a top-level
DLL retains its own discovery/shadow-copy boundary unless it is inside one of those
private trees. Dependencies are resolved in collectible, shadow-copied contexts,
with contract, rendering and Dalamud/core assemblies shared with the default context.
Filesystem watching and reload are disabled for normal desktop startup. Synchronous
disposal joins one shared async shutdown task; lifecycle awaits are context-independent,
plugin actions take per-entry leases, and unload waits for active leases before
requesting collectible-context unload. Windows file-lock cleanup retries after unload
and remains subject to the native Windows acceptance matrix.

## Deferred integration work

### FFXIV game patching and updating

The FFXIV launch path currently reads the local `ffxivgame.ver` as part of session/patch-identity acquisition, authenticates, and starts the game process. It does **not yet provide a complete WebLaunch-managed game patch/update workflow** that discovers required official game updates, obtains patch metadata and payloads, downloads and validates them, applies them safely, recovers interrupted application, and gates launch on successful completion. The current `FFXIVGamePlugin` reaches authentication and then `networklogic.LaunchGameAsync`; this is not an update orchestration path.

The vendored `LibDalamud` source contains a substantial upstream-style game-patching stack, including `XIVLauncher.Common.Game.Launcher` boot/game patch checks and patch-list handling, `PatchManager`, `PatchVerifier`, `PatchInstaller`, download-acquisition implementations, and ZiPatch/indexed-patch machinery. It also contains a separate `DalamudUpdater` for Dalamud, its .NET runtime, and assets. These components are not currently orchestrated by WebLaunch's `FFXIVGamePlugin` launch path: that path calls `networklogic.GetRealSidAsync` and then `networklogic.LaunchGameAsync`, and does not run the vendored launcher's login/patch-check/update flow before launching. Therefore the engine code exists in the tree, but end-to-end FFXIV game patching/updating is still **not implemented in the active WebLaunch flow**. A follow-up should first reconcile these components with selected XIVLauncher upstream revisions, then integrate the maintained patch pipeline instead of inventing a parallel patcher or blindly transplanting upstream files.

That work needs explicit boot/game version tracking, official patch metadata acquisition, bounded and cancellable downloads, integrity validation, contained patch application, durable crash recovery/repair, clear progress/error reporting, and a launch gate that prevents authentication/startup against an incomplete installation. Tests should exercise patch metadata/version chains, malformed or stale manifests, checksum failures, interrupted and partially applied patches, locked files, cancellation, disk/permission failures, and recovery without live-game credentials; authorized native-Windows/game acceptance remains separate. This capability is deferred from the current infrastructure PR.

### Upstream Dalamud/XIVLauncher synchronization

The vendored `LibDalamud` implementation is a maintained fork, not a substitute for
periodic upstream integration. A separate synchronization task must compare the fork
with the relevant XIVLauncher/Dalamud upstream revisions and selectively port the
changes required by WebLaunch. Priority areas include FFXIV authentication/session
handling, game launch and process ownership, Dalamud bootstrap/injector behavior,
version/runtime acquisition, and patch/update behavior. The sync should preserve
WebLaunch-specific interfaces and lifecycle guarantees where needed, document intentional
divergence, and add regression coverage around changed authentication and launch paths.
Do not perform a blind source-tree overwrite or bundle this work into infrastructure-only
PRs: upstream changes need review and native Windows acceptance before they are treated
as equivalent to XIVLauncher behavior.

### Runtime plugin loading without concrete host references

Dynamic discovery/loading already exists in `CoreFunctions`, but `handlerlaunch` still
has build-time project references to the bundled FFXIV and Spellborn plugin projects.
The target is **dynamic-first loading**, with an explicit static/built-in fallback kept
for compatibility with deployments where a bundled plugin still arrives via an assembly
reference. It must not be possible to register both implementations for one plugin ID,
and fallback must not silently hide an integrity, API-compatibility or initialization
failure.

The dedicated loader change should provide a validated plugin catalog/manifest, preserve
the bundle root separately from the implementation assembly path, resolve private managed
and native dependencies through isolated collectible load contexts, share only version-
compatible host contract assemblies, validate a dependency graph with required/optional
edges and version constraints, and load in deterministic dependency order. Per-plugin
load/initialization failures must roll back partial state without aborting unrelated
plugins. Leases and explicit retirement must coordinate calls, reload, shutdown and
unload; a new version should be staged before replacing the working version. Static
fallback candidates should be explicitly registered and selected under a documented
source-precedence policy, not discovered by indiscriminately scanning the default
load context.

The solution/build pipeline may still build and package bundled plugin projects; the
intended decoupling is from the host's compile-time references, not from building or
shipping the default plugins. Acceptance must prove both modes: a plugin absent from
the host reference graph is discovered, loaded, used and unloaded, and the registered
reference fallback works when its dynamic bundle is absent. Also test duplicate IDs,
malformed/path-escaping manifests, symlinks/reparse points, nested resources, private
dependencies, missing/cyclic/incompatible dependencies, partial initialization,
dynamic-vs-static precedence, failed hot reload rollback and actual Windows file-lock
cleanup. An `AssemblyLoadContext` is not a sandbox for untrusted code.

See [the full plugin-loading roadmap](plugin-loading-roadmap.md). Keep this redesign out
of the present infrastructure PR and implement it as a dedicated, testable architecture
change.

## Updates and recovery

All remote relative paths are resolved beneath their selected root. Windows device
names, traversal, UNC/device paths, alternate streams and existing reparse points
are rejected. This guards untrusted downloaded names; it cannot protect against a
privileged local process replacing directories concurrently.

ZIP files are prevalidated and extracted into `.weblaunch-transaction/stage`.
Before replacing each target, the flushed recovery journal records its previous
state; originals move to the backup directory. Cancellation or failure rolls back
replacements. For version-aware updates, the journal also records the previous
version marker. The updater commits the marker only after replacing the files, then
persists the committed marker in the journal. If metadata persistence fails after a
partial write, recovery restores the previous version and rolls file replacements
back. A later update supplies the metadata-restore policy before staging a new
transaction. Recovery metadata/backups are retained if rollback fails. A journal-less
staging directory requires inspection; the installer does not guess what to delete.
The legacy callback overload remains for compatibility, but callers that need
coordinated metadata rollback should use `VersionMetadataTransaction`.

Provider MD5/SHA1 fields in the implemented Spellborn update path remain compatibility integrity checks, not signatures.
TLS certificate verification is enabled. Downloads and asynchronous updater calls
have bounded retries/cancellation. The vendored FFXIV ZiPatch/patch IPC components
remain available as lower-level primitives, but a complete WebLaunch-managed FFXIV
game patch/update workflow is deferred; do not infer it from the Spellborn archive
installer or the separate Dalamud/runtime/assets updater.

## Browser sections and component libraries

MasaBlazor supplies page content; LumexUI v2 supplies navigation, links and controls.
`GamePageBase` normalizes old route/query section links for both games to base-aware
fragments. `GameNavigation` owns the route and selected section, including refresh
and Back/Forward. A scoped JS adapter reports hash-only anchor/history changes
that Blazor does not surface through its managed location event. Browser reports
carry monotonically increasing revisions, so an older asynchronous callback cannot
overwrite a newer fragment selection. Unknown/non-section fragments preserve a
valid active section instead of accidentally scrolling to an unrelated section.
It clears the subheader when leaving a game; it never redirects section selection
to the library.

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

Folder browsing is an optional, authenticated bridge capability. The browser uses
the desktop's native chooser because browser directory handles do not expose a
reliable absolute Windows path. Shared validation is applied on both sides, and
manual path entry remains supported. Browser diagnostics emit only operation/error
types and fixed codes; they never format exception messages, stacks, URLs or state.
Theme/storage faults are contained locally; a structural navigation-sync fault is
recorded in sanitized diagnostics and escalated to the safe render boundary, whose
recovery screen lets the user reload the launcher or return to the library.
Owned JavaScript modules and .NET references are released independently with nested
cleanup paths even if the browser has disconnected. Status checks tolerate transient
interop failures before showing reconnect instructions; after launch submission,
ambiguous outcomes require a fresh paired session before another launch to avoid
duplicating a game process. Neither browser storage nor recovery UI persists sign-in
details.

### Desktop progress and diagnostics

`ObservedLaunchService` provides the same safe lifecycle events to GUI/console
status displays, Windows notifications and diagnostic files. Plugin progress cannot
claim terminal success; success is emitted only after the launch service returns.
Observer failures are isolated from the launch. Windows notifications use
`Windows.UI.Notifications.ToastNotificationManager` with a per-request tag and a
Start-menu application identity installed only by explicit install/repair. Protocol
activation uses a v2 bootstrap without secrets. Quiet mode records notifications
without popup banners. Native notification behavior remains a Windows acceptance gate.

`SafeDiagnosticLog` stores controlled events in two bounded files. Debug mode adds
plugin trace events but never accepts raw message text or exception payloads. Unknown
plugin IDs and states become fixed labels. Browser diagnostics similarly avoid
formatting arbitrary state; .NET stderr is avoided for handled errors because it
triggers Blazor's fatal-error banner.

The Windows API registration follows Microsoft's desktop notification requirements:
[CreateToastNotifier(appID)](https://learn.microsoft.com/en-us/uwp/api/windows.ui.notifications.toastnotificationmanager.createtoastnotifier)
and [desktop AppUserModelID shortcut registration](https://learn.microsoft.com/en-us/previous-versions/windows/desktop/legacy/hh802768(v=vs.85)).
