param([string]$YakPath, [switch]$ReplaceFull)
$ErrorActionPreference = 'Stop'
if (Get-Process -Name Rhino -ErrorAction SilentlyContinue) { throw '请保存工作并关闭所有 Rhino 窗口，再安装。' }
if (!$YakPath) {
    $install = Get-ItemProperty -LiteralPath 'HKLM:\SOFTWARE\McNeel\Rhinoceros\9.0\Install' -ErrorAction Stop
    $rhinoPath = $install.ExePath
    if (!$rhinoPath) { $rhinoPath = Join-Path $install.Path 'Rhino.exe' }
    $YakPath = Join-Path (Split-Path -Parent $rhinoPath) 'Yak.exe'
}
if (!(Test-Path -LiteralPath $YakPath -PathType Leaf)) { throw '未找到 Rhino 9 的 Yak.exe；请通过 -YakPath 指定。' }
$packages = @(Get-ChildItem -LiteralPath $PSScriptRoot -Filter 'miki-rhino-*-rh9_0-win.yak' -File)
if ($packages.Count -ne 1 -or $packages[0].Name -notmatch '^miki-rhino-(\d+\.\d+\.\d+)-rh9_0-win\.yak$') { throw '请先解压完整预览包，且目录中只能有一个 Miki-Rhino Yak。' }
$package = $packages[0]
$version = $Matches[1]
$checksum = Join-Path $PSScriptRoot 'SHA256SUMS.txt'
if (!(Test-Path -LiteralPath $checksum -PathType Leaf)) { throw '缺少 SHA256SUMS.txt。' }
$expected = @(Get-Content -LiteralPath $checksum | Where-Object { $_ -match ('^([a-f0-9]{64})  ' + [regex]::Escape($package.Name) + '$') })
if ($expected.Count -ne 1 -or (Get-FileHash -LiteralPath $package.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -ne $expected[0].Substring(0,64)) { throw '预览包校验失败。' }
$installedRoot = Join-Path $env:APPDATA 'McNeel/Rhinoceros/packages/9.0/miki-rhino'
if (!$ReplaceFull -and (Test-Path -LiteralPath $installedRoot)) {
    foreach ($item in Get-ChildItem -LiteralPath $installedRoot -Directory) {
        if (Test-Path -LiteralPath (Join-Path $item.FullName 'openbim/runtime/manifest.json') -PathType Leaf) {
            throw '已发现包含 IFC 的完整测试版；预览版不含 IFC，默认不会覆盖。'
        }
    }
}
$verifier = Join-Path $PSScriptRoot 'verify-yak-install.ps1'
if (!(Test-Path -LiteralPath $verifier -PathType Leaf)) { throw '缺少安装校验脚本，请重新解压完整预览包。' }
if (Get-Process -Name Rhino -ErrorAction SilentlyContinue) { throw 'Rhino 已打开；请先保存工作并关闭后再安装。' }
& $YakPath install $package.FullName
if ($LASTEXITCODE -ne 0) { throw 'Yak 安装失败。' }
& $verifier -PackagePath $package.FullName -InstalledDirectory (Join-Path $installedRoot $version)
Write-Output 'Miki-Rhino 预览版已安装。重启 Rhino，打开 AI 面板并选择 Miki-Rhino；此版本不支持 IFC 导出。'
