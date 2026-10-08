# Third-party components

WebLaunch includes adapted XIVLauncher/Dalamud integration and patching code under
`LibDalamud` and `CoreLibLaunchSupport`. Retain their source notices. Dalamud's
runtime, injector, assets and addon lifecycle are separate from the Elgarnan
application plugin host. Updating one does not replace the other.

Desktop bundles are generated from the NuGet dependencies declared in the project
files, including ImGui.NET, Veldrid, SDL2, ImageSharp, Steamworks and the .NET
runtime. The previous checked-in `Plugins/FFXIV` directory was build output;
`scripts/package.ps1` now publishes those dependencies and native assets from
NuGet. Keep NuGet/package license and notice files in redistributed bundles.
The license expressions and license links in the restored package metadata are
the source of truth for each dependency; WebLaunch's MIT license does not replace
them. No vendored source/resource notices are removed by this refactor.
