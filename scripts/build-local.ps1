param([string]$IfcRuntimePath, [switch]$Offline)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$stagingRoot = Join-Path $repository 'dist/local'
$stage = [IO.Path]::GetFullPath((Join-Path $stagingRoot ([guid]::NewGuid().ToString('N'))))
if (!$stage.StartsWith([IO.Path]::GetFullPath($stagingRoot) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected local build directory.' }
New-Item -ItemType Directory -Path $stage | Out-Null
$project = Join-Path $repository 'src/RhinoMagpie.Plugin/RhinoMagpie.Plugin.csproj'
$restoreArguments = @('restore', $project, '--configfile', (Join-Path $repository 'NuGet.Config'), '-p:NuGetAudit=false')
if ($Offline) {
    $localPackageCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path $env:USERPROFILE '.nuget/packages' }
    $restoreArguments += @('--source', $localPackageCache)
}
& dotnet @restoreArguments
if ($LASTEXITCODE -ne 0) { throw 'Dependency restoration failed. Normal builds use nuget.org; -Offline requires a complete local cache.' }
& dotnet build $project --no-restore --nologo -c Release -t:Rebuild -p:DebugType=None -p:DebugSymbols=false "-p:PathMap=$repository=/_/Miki-Rhino" -o $stage
if ($LASTEXITCODE -ne 0) { throw 'Miki-Rhino compilation failed.' }
$binaryNames = @('MikiRhino.Plugin.rhp', 'MikiRhino.Agent.dll', 'MikiRhino.OpenBim.dll', 'MikiRhino.OpenBim.Rhino.dll')
$binaries = @($binaryNames | ForEach-Object { Join-Path $stage $_ })
& (Join-Path $PSScriptRoot 'check-managed-debug-paths.ps1') -Binaries $binaries
if ($IfcRuntimePath) {
    $runtime = (Resolve-Path -LiteralPath $IfcRuntimePath).Path
    $runtimeManifest = Join-Path $runtime 'manifest.json'
    $manifest = Get-Content -LiteralPath $runtimeManifest -Raw | ConvertFrom-Json
    $runtimeDestination = Join-Path $stage 'openbim/runtime'
    New-Item -ItemType Directory -Path $runtimeDestination | Out-Null
    foreach ($relative in (@($manifest.files.path) + @('manifest.json'))) {
        $target = [IO.Path]::GetFullPath((Join-Path $runtimeDestination $relative))
        if (!$target.StartsWith([IO.Path]::GetFullPath($runtimeDestination) + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime path escaped staging.' }
        New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
        Copy-Item -LiteralPath (Join-Path $runtime $relative) -Destination $target
    }
    Add-Type -Path (Join-Path $stage 'MikiRhino.Agent.dll')
    $location = [RhinoMagpie.Agent.IfcRuntimeBundle]::Resolve($stage)
    Write-Output ('Verified copied IFC runtime ' + $location.PythonVersion + ' / ' + $location.IfcVersion)
}
Copy-Item -LiteralPath (Join-Path $repository 'README.md'), (Join-Path $repository 'LICENSE') -Destination $stage
Copy-Item -LiteralPath (Join-Path $repository 'rhino-package/manifest.yml') -Destination (Join-Path $stage 'manifest.yml')
$hashes = @($binaries | ForEach-Object { @{file=[IO.Path]::GetFileName($_); sha256=(Get-FileHash -LiteralPath $_ -Algorithm SHA256).Hash.ToLowerInvariant()} })
@{product='Miki-Rhino'; version='0.1.0'; localBuild=$true; managedDebugPathsVerified=$true; runtimeIncluded=[bool]$IfcRuntimePath; binaries=$hashes} |
    ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $stage 'BUILD_REPORT.json') -Encoding utf8NoBOM
@{directory=[IO.Path]::GetRelativePath($repository, $stage); product='Miki-Rhino'; version='0.1.0'} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $repository 'dist/LATEST_BUILD.json') -Encoding utf8NoBOM
Write-Output ('MIKI_BUILD_DIRECTORY=' + $stage)
