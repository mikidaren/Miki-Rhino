param([Parameter(Mandatory=$true)][string]$PackagePath, [Parameter(Mandatory=$true)][string]$InstalledDirectory)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$root = [IO.Path]::GetFullPath((Resolve-Path -LiteralPath $InstalledDirectory).Path).TrimEnd([char[]]@('\','/')) + [IO.Path]::DirectorySeparatorChar
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PackagePath).Path)
$hasher = [Security.Cryptography.SHA256]::Create()
$count = 0
$seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
try {
    foreach ($entry in $archive.Entries) {
        $relative = $entry.FullName
        if ($relative.EndsWith('/')) { continue }
        $parts = $relative.Split('/')
        if (!$relative -or $relative.Contains('\') -or [IO.Path]::IsPathRooted($relative) -or @($parts | Where-Object { !$_ -or $_ -eq '.' -or $_ -eq '..' -or $_.Contains(':') }).Count -gt 0 -or !$seen.Add($relative)) { throw 'Yak 包含不安全或重复的文件路径。' }
        $target = [IO.Path]::GetFullPath((Join-Path $root ($relative.Replace('/', [IO.Path]::DirectorySeparatorChar))))
        if (!$target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase) -or !(Test-Path -LiteralPath $target -PathType Leaf)) { throw ('安装后缺少文件：' + $relative) }
        $packaged = $entry.Open()
        try { $expected = [Convert]::ToBase64String($hasher.ComputeHash($packaged)) } finally { $packaged.Dispose() }
        $installed = [IO.File]::OpenRead($target)
        try { $actual = [Convert]::ToBase64String($hasher.ComputeHash($installed)) } finally { $installed.Dispose() }
        if ($actual -ne $expected) { throw ('安装文件校验失败：' + $relative) }
        $count++
    }
    if ($count -eq 0) { throw 'Yak 中没有文件。' }
} finally { $hasher.Dispose(); $archive.Dispose() }
Write-Output "已核对 $count 个已安装文件。"
