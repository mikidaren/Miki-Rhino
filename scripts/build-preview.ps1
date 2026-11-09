param([Parameter(Mandatory=$true)][string]$YakPath, [switch]$Offline, [switch]$AllowDirty)
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$yak = (Resolve-Path -LiteralPath $YakPath).Path
$version = ((Select-String -LiteralPath (Join-Path $repository 'rhino-package/manifest.yml') -Pattern '^version:\s*(.+)$').Matches.Groups[1].Value).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw 'Invalid preview package version.' }

function Read-SourceIdentity {
    $commit = (& git -C $repository rev-parse --verify HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $commit -notmatch '^[a-f0-9]{40}$') { throw 'A Git commit is required.' }
    $changes = @(& git -C $repository status --porcelain=v1 --untracked-files=all)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to inspect source state.' }
    return @{ commit=$commit; clean=($changes.Count -eq 0) }
}
$before = Read-SourceIdentity
if (!$before.clean -and !$AllowDirty) { throw 'Public preview builds require a clean source tree.' }
& (Join-Path $PSScriptRoot 'build-local.ps1') -Offline:$Offline
$latest = Get-Content -LiteralPath (Join-Path $repository 'dist/LATEST_BUILD.json') -Raw | ConvertFrom-Json
$compiled = [IO.Path]::GetFullPath((Join-Path $repository $latest.directory))
if (!$compiled.StartsWith((Join-Path $repository 'dist/local') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Unexpected compiled output.' }
$buildReport = Get-Content -LiteralPath (Join-Path $compiled 'BUILD_REPORT.json') -Raw | ConvertFrom-Json
if ($buildReport.runtimeIncluded -or $buildReport.version -ne $version) { throw 'Preview must be built without an IFC runtime and with the matching version.' }
$stage = Join-Path $repository ('dist/preview/staging/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
$files = @('MikiRhino.Plugin.rhp', 'MikiRhino.Agent.dll', 'MikiRhino.OpenBim.dll', 'MikiRhino.OpenBim.Rhino.dll', 'MikiRhino.Plugin.deps.json', 'MikiRhino.Plugin.runtimeconfig.json', 'README.md', 'LICENSE', 'manifest.yml')
foreach ($file in $files) { Copy-Item -LiteralPath (Join-Path $compiled $file) -Destination $stage }
$after = Read-SourceIdentity
if ($before.commit -ne $after.commit -or (!$AllowDirty -and (!$before.clean -or !$after.clean))) { throw 'Source changed during build.' }
@{ product='Miki-Rhino'; version=$version; sourceCommit=$before.commit; sourceClean=($before.clean -and $after.clean); ifcRuntimeIncluded=$false; ifcExportAvailable=$false; kind='limited-preview' } |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $stage 'BUILD_PROVENANCE.json') -Encoding utf8NoBOM
$expected = @($files + 'BUILD_PROVENANCE.json' | Sort-Object)
Push-Location $stage
try {
    & $yak build --platform win
    if ($LASTEXITCODE -ne 0) { throw 'Yak preview packaging failed.' }
} finally { Pop-Location }
$package = Get-ChildItem -LiteralPath $stage -Filter "miki-rhino-$version-*.yak"
if (@($package).Count -ne 1) { throw 'Expected one preview Yak.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [IO.Compression.ZipFile]::OpenRead($package.FullName)
try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName } | Sort-Object)
    if (($entries -join "`n") -cne ($expected -join "`n")) { throw 'Preview Yak contains unexpected or missing files.' }
} finally { $archive.Dispose() }
$release = Join-Path $repository ("dist/preview/release/$version")
New-Item -ItemType Directory -Path $release -Force | Out-Null
$destination = Join-Path $release $package.Name
Copy-Item -LiteralPath $package.FullName -Destination $destination -Force
"$((Get-FileHash -LiteralPath $destination -Algorithm SHA256).Hash.ToLowerInvariant())  $($package.Name)" |
    Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding utf8NoBOM
$zip = Join-Path $release ("Miki-Rhino-$version-preview-win-x64.zip")
Compress-Archive -LiteralPath $destination,(Join-Path $release 'SHA256SUMS.txt'),(Join-Path $PSScriptRoot 'install-preview.ps1'),(Join-Path $PSScriptRoot 'verify-yak-install.ps1'),(Join-Path $repository 'README.md') -DestinationPath $zip -Force
Get-FileHash -LiteralPath $destination,$zip -Algorithm SHA256 | ForEach-Object {
    "$($_.Hash.ToLowerInvariant())  $(Split-Path -Leaf $_.Path)"
} | Set-Content -LiteralPath (Join-Path $release 'SHA256SUMS.txt') -Encoding utf8NoBOM
Write-Output ('MIKI_PREVIEW_YAK=' + $destination)
Write-Output ('MIKI_PREVIEW_ZIP=' + $zip)
