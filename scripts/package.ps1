$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$output = Join-Path $root 'artifacts/desktop'
if (Test-Path $output) { Remove-Item -Recurse -Force $output }
dotnet publish handlerlaunch/handlerlaunch.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=false -p:PublishReadyToRun=false -o $output
if ($LASTEXITCODE -ne 0) { throw 'Desktop publish failed' }
foreach ($game in @('FFXIV', 'Spellborn')) {
    $project = "Gameplugins.Plugin.$game/Gameplugins.Plugin.$game.csproj"
    $pluginOutput = Join-Path $output "Plugins/$game"
    dotnet publish $project -c Release -r win-x64 --self-contained false -o $pluginOutput
    if ($LASTEXITCODE -ne 0) { throw "$game plugin publish failed" }
    Copy-Item "Gameplugins.Plugin.$game/plugin.json" $pluginOutput
}
Copy-Item LICENSE $output
Copy-Item docs/THIRD-PARTY.md $output
# Preserve package metadata and the license/notice files shipped by each dependency.
$noticeRoot = Join-Path $output 'third-party-notices'
Get-ChildItem -Path $root -Filter project.assets.json -Recurse | Where-Object { $_.FullName -notmatch '[\\/]artifacts[\\/]' } | ForEach-Object {
    $assets = Get-Content $_.FullName -Raw | ConvertFrom-Json
    foreach ($library in $assets.libraries.PSObject.Properties) {
        if ($library.Value.type -ne 'package') { continue }
        foreach ($folder in $assets.packageFolders.PSObject.Properties.Name) {
            $package = Join-Path $folder $library.Value.path
            if (-not (Test-Path $package)) { continue }
            Get-ChildItem $package -Recurse -File | Where-Object { $_.Extension -eq '.nuspec' -or $_.Name -match '^(LICENSE|NOTICE|COPYING)' } | ForEach-Object {
                $relative = [IO.Path]::GetRelativePath($package, $_.FullName)
                $destination = Join-Path (Join-Path $noticeRoot $library.Value.path) $relative
                New-Item -ItemType Directory -Force -Path (Split-Path $destination) | Out-Null
                Copy-Item $_.FullName $destination -Force
            }
        }
    }
}
Compress-Archive -Path "$output/*" -DestinationPath artifacts/WebLaunch-win-x64.zip -Force
