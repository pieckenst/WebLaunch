# Plugin loading and dependency-resolution roadmap

Status: **deferred design**, not a claim that this architecture has already been implemented.

## Goal and compatibility contract

WebLaunch should make **runtime-discovered plugin bundles the primary path**. A plugin implementation should be buildable and shippable without a `ProjectReference` from `handlerlaunch` to that implementation. The solution may still build the bundled FFXIV and Spellborn plugin projects and package their output.

There must also be a deliberate compatibility path for a deployment where a bundled plugin is supplied through a static project/assembly reference. Both sources implement the same plugin contract and lifecycle. The fallback path must not become an accidental second implementation of every plugin, and it must not cause duplicate plugin IDs or duplicate contract assemblies.

The existing `CoreFunctions` directory discovery, manifest/legacy loading, shadow-copying and collectible `AssemblyLoadContext` are a starting point, not the complete design. This is roadmap scope for a dedicated change after the current infrastructure PR.

## Proposed architecture

### 1. Separate the host contract from implementation packaging

- Keep a small, stable contract assembly for `IGamePlugin`, lifecycle models, dependency declarations, logging and capability interfaces. Its assembly identity must be shared by all plugins and the host.
- `handlerlaunch` owns the plugin-host lifecycle and a loader/catalog abstraction. It should not need compile-time references to ordinary plugin implementation projects.
- Each plugin is packaged as a self-describing bundle: a versioned manifest, implementation assembly, private managed dependencies, native/runtime dependencies, satellite/resource assemblies and data files.
- The packaging pipeline may reference plugin projects and copy their publish outputs into the release bundle. Packaging dependencies do not imply that `handlerlaunch` references the implementations.
- Expose an explicit static/built-in registration seam for legacy deployment compatibility. Do not discover static plugins by scanning every assembly already loaded in the process: make fallback candidates an intentional registry with known IDs and contract versions.

### 2. Deterministic source precedence and fallback policy

For a plugin ID with a dynamic bundle and a statically registered fallback, source selection must be documented and deterministic. The intended policy is:

1. Validate discovery candidates and prefer a valid dynamic bundle for the ID.
2. If the dynamic artifact is absent, use its registered bundled fallback when that plugin is required and the fallback's contract/API range is compatible.
3. If the dynamic artifact is present but rejected for a security or integrity reason (path/link violation, malformed manifest, hash/signature failure when signing is introduced), fail closed and report the failure; do not silently run a possibly stale fallback.
4. Treat dependency/API conflicts, initialization failures and plugin-code exceptions as explicit load failures. Do not mask programming errors by silently switching implementations. A narrowly defined fallback-on-load-failure behavior may be configured for a specific built-in plugin only if the policy is explicit, the dynamic candidate is quarantined for that attempt, and both the failed attempt and selected source are reported.
5. Never register two implementations for the same stable plugin ID. If a dynamic plugin replaces a built-in, fully validate it before making it active, and keep the fallback unavailable until the dynamic candidate has either committed or been rejected.
6. Surface the selected source (dynamic bundle or registered fallback), chosen version and controlled failure code in diagnostics. Avoid raw filesystem paths and plugin-controlled message text in normal logs.

The default behavior should be **dynamic-first**; the static path is a compatibility/recovery mechanism, not the ordinary launch path.

## Manifest and candidate validation

Use a versioned manifest with, at minimum:

- Stable plugin ID and plugin version.
- Manifest schema version and supported host contract/API range.
- Implementation assembly path relative to the bundle root.
- Required and optional plugin dependencies, each with version constraints.
- Private assembly/native asset metadata and optional integrity hashes.
- A declared load policy/capability set where the host needs to decide whether optional facilities are permitted.

Validate the manifest and candidate before invoking plugin code. Reject absolute paths, traversal, device/UNC paths where relevant, invalid assembly paths, links/reparse points at every path component, duplicate IDs, incompatible contract versions, malformed ranges and unsupported manifest schemas. Resolve every file under the known bundle root, and preserve the root separately from the implementation assembly path so assemblies may reside in subdirectories without losing sibling resources.

Integrity checks in the package format must be distinguished from publisher authenticity. A checksum alone is not a signature. If remote/untrusted plugin installation is later added, signature/key rotation and trust/revocation are a separate requirement.

## Dependency resolution and isolation

- Create a collectible `AssemblyLoadContext` per plugin bundle, or per explicitly version-locked dependency graph when plugins share private dependencies. Never load arbitrary private dependency assemblies into the default context.
- Use `AssemblyDependencyResolver` for managed and native assets and preserve bundle-relative layout so resources, satellites and RID-specific libraries resolve. Avoid a blanket fallback to whichever DLL happens to exist next to the executable; it can bind a plugin to an incompatible host dependency.
- Share only deliberate contract/framework/service assembly identities from the default context. Compare requested identity and version against the shared contract policy instead of matching names alone. Multiple copies of the contract in separate load contexts would break interface/type identity and can make valid plugins appear unrelated to `IGamePlugin`.
- Declare required versus optional dependencies. Build a dependency graph, check version ranges, detect cycles and duplicate/conflicting providers, then initialize in deterministic topological order. Missing required dependencies block the dependent plugin; optional dependencies produce an explicit capability downgrade. Unload dependent plugins before a provider they use.
- If several plugins need incompatible versions of the same library, prefer bundle isolation. If the runtime cannot safely support that combination, reject the conflicting candidate with a useful diagnostic instead of choosing an arbitrary version.
- Resolve native dependencies from the plugin's declared bundle/RID paths. Do not invoke PATH-wide searches or accept arbitrary directories as dependency sources. On Windows, test transitive native dependency probing and shadow-copy/file-lock behavior.

## Load transaction, failure isolation and lifecycle

Treat load as a transaction: discover -> parse/validate -> stage/shadow-copy -> resolve dependencies -> construct -> validate configuration -> initialize -> atomically register. If any phase fails, stop/dispose any partially constructed instances, unload the context, remove the staged files when safe, and leave the previous active implementation intact. One bad plugin must not abort discovery of unrelated plugins.

Required behavior:

- Report typed error categories (manifest, compatibility, dependency, constructor, initialization, renderer, shutdown, file access) with correlation IDs and safe metadata. Keep full exception detail behind explicit local diagnostic mode.
- Retry dependency-delayed candidates only when a provider actually becomes available. Detect no-progress/cycle states and report the unresolved graph; do not loop indefinitely.
- Serialize registry transitions, but do not hold registry/lifecycle locks while running arbitrary long plugin actions or callbacks that can re-enter the host. Use leases/retirement to stop new calls and drain in-flight calls before unloading.
- Link cancellation to the host lifetime where an API accepts a token. Cancellation is cooperative: a plugin that ignores it must not cause the host to kill unrelated game processes or pretend the assembly has unloaded.
- On reload, stage and validate the replacement first. For a successful replacement, retire the old instance, wait for leases, run shutdown/disposal and unload the old context. If staging fails, retain the previous version instead of leaving no working plugin.
- Handle event subscriptions, timers, background tasks, renderer references, native handles and callbacks. A collectible context is only eligible for GC when nothing outside it references its objects; `Unload()` does not force or guarantee unloading.
- Bound retry attempts and cleanup waits. Defer stubborn shadow-directory deletion to a background reaper without deleting a directory whose lock indicates another live host owns it.
- Be explicit that in-process plugins have host-user privileges; `AssemblyLoadContext` is a dependency/isolation mechanism, **not a security sandbox**. Untrusted third-party plugins need process isolation and IPC, not just a new loader.

## Legacy compatibility

- Preserve the current legacy DLL discovery mode for layouts that predate manifests, including assembly-relative assets and known private dependency folders. Preserve source bundle root and relative paths.
- Treat an encountered nested `plugin.json` as an explicit child bundle boundary even inside a conventional resources/runtime tree. For manifestless nested DLL directories, retain the documented compatibility heuristic and add tests for its known ambiguity.
- Do not switch blindly to recursive copy of every file: it may merge another plugin into its parent bundle. Reject links and validate each copied path.
- Add a migration guide from legacy layouts to a manifest. Keep historical binary fixtures where legally available and distinguish tests of the source contract from proof of binary compatibility.

## Regression and acceptance checklist

- [ ] Load a dynamic plugin whose implementation is not in `handlerlaunch`'s project-reference graph.
- [ ] Load the same built-in via the registered static fallback when its dynamic bundle is absent.
- [ ] Verify dynamic-first precedence, one instance per ID, deterministic version choice, and no accidental fallback after a manifest/security rejection.
- [ ] Verify loader diagnostics identify source, version and controlled error category without leaking raw paths, credentials or plugin messages.
- [ ] Test manifest schema/version/API compatibility, traversal, absolute paths, links/reparse points, invalid assembly names and hashes.
- [ ] Test nested implementation assembly, private managed dependencies, resource/satellite assemblies, native DLLs and RID assets on Windows.
- [ ] Test missing required dependency, optional dependency downgrade, version conflict, dependency cycle, dependent/provider ordering and retry termination.
- [ ] Test constructor and partial-initialization exceptions, failure isolation, complete rollback, and preservation of the previous plugin on failed hot reload.
- [ ] Test concurrent use/unload, long-lived leases, cancellation, reentrant callbacks, event/task cleanup, renderer failure/restart and duplicate initialization.
- [ ] Verify collectible-context cleanup with weak references and actual Windows shadow-file replacement/deletion; do not make GC timing alone a brittle unit-test assertion.
- [ ] Verify packaged release contents without depending on host project references, and separately exercise legacy static-reference compatibility.
- [ ] Document every known limitation in the final binary/native-plugin compatibility matrix.

## Implementation order

1. Freeze the contract and define manifest/API version semantics.
2. Introduce the catalog/candidate model and dependency graph validator without changing the host's current source selection.
3. Add dynamic-first bundles and the explicit static fallback registry; migrate one bundled plugin as the proof of concept.
4. Prove no host project reference is needed for the dynamically packaged implementation, while the reference fallback continues to run.
5. Migrate the remaining bundled plugins, update release packaging and legacy migration docs, then add external-plugin support only if its trust/compatibility policy is separately accepted.
