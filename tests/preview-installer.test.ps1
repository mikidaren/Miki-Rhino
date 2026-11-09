param([Parameter(Mandatory=$true)][string]$PreviewZip)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$testRoot = Join-Path $repository ('dist/preview-installer-test/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
Expand-Archive -LiteralPath $PreviewZip -DestinationPath $testRoot
$installer = Join-Path $testRoot 'install-preview.ps1'
$fakeYak = Join-Path $testRoot 'fake-yak.ps1'
@'
param([string]$Action, [string]$Package)
if ($Action -ne 'install') { throw 'Unexpected fake Yak operation.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
if ([IO.Path]::GetFileName($Package) -notmatch '^miki-rhino-(\d+\.\d+\.\d+)-rh9_0-win\.yak$') { throw 'Unexpected package name.' }
$target = Join-Path $env:APPDATA ('McNeel/Rhinoceros/packages/9.0/miki-rhino/' + $Matches[1])
[IO.Compression.ZipFile]::ExtractToDirectory($Package, $target)
Set-Content -LiteralPath (Join-Path $env:MIKI_PREVIEW_INSTALL_TEST_ROOT 'yak-called.txt') -Value 'installed'
$global:LASTEXITCODE = 0
'@ | Set-Content -LiteralPath $fakeYak -Encoding utf8NoBOM
$oldAppData = $env:APPDATA
$oldTestRoot = $env:MIKI_PREVIEW_INSTALL_TEST_ROOT
try {
    $env:APPDATA = Join-Path $testRoot 'AppData'
    $env:MIKI_PREVIEW_INSTALL_TEST_ROOT = $testRoot
    $sentinel = Join-Path $env:APPDATA 'McNeel/Rhinoceros/packages/9.0/miki-rhino/0.1.0/openbim/runtime/manifest.json'
    New-Item -ItemType Directory -Path (Split-Path -Parent $sentinel) -Force | Out-Null
    Set-Content -LiteralPath $sentinel -Value 'full runtime marker'
    $protected = $false
    try { & $installer -YakPath $fakeYak } catch { $protected = $_.Exception.Message -match '完整测试版' }
    if (!$protected -or !(Test-Path -LiteralPath $sentinel) -or (Test-Path -LiteralPath (Join-Path $testRoot 'yak-called.txt'))) { throw 'Installer did not protect an existing full runtime.' }
    Remove-Item -LiteralPath $sentinel
    & $installer -YakPath $fakeYak
    if (!(Test-Path -LiteralPath (Join-Path $testRoot 'yak-called.txt'))) { throw 'Installer did not invoke Yak for a clean profile.' }
    Remove-Item -LiteralPath (Join-Path $testRoot 'yak-called.txt')
    $yakPackage = @(Get-ChildItem -LiteralPath $testRoot -Filter 'miki-rhino-*-rh9_0-win.yak')[0]
    [IO.File]::AppendAllText($yakPackage.FullName, 'tampered')
    $refused = $false
    try { & $installer -YakPath $fakeYak } catch { $refused = $_.Exception.Message -match '校验失败' }
    if (!$refused -or (Test-Path -LiteralPath (Join-Path $testRoot 'yak-called.txt'))) { throw 'Installer did not refuse the altered package.' }
    Write-Output 'PREVIEW_INSTALLER_SAFETY_PASSED'
} finally {
    $env:APPDATA = $oldAppData
    $env:MIKI_PREVIEW_INSTALL_TEST_ROOT = $oldTestRoot
}
