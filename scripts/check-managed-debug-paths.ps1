param([Parameter(Mandatory=$true)][string[]]$Binaries)
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Reflection.Metadata
foreach ($binary in $Binaries) {
    $path = (Resolve-Path -LiteralPath $binary).Path
    $stream = [IO.File]::OpenRead($path)
    try {
        $reader = [System.Reflection.PortableExecutable.PEReader]::new($stream)
        try {
            foreach ($entry in $reader.ReadDebugDirectory()) {
                if ($entry.Type -ne [System.Reflection.PortableExecutable.DebugDirectoryEntryType]::CodeView) { continue }
                $debugPath = $reader.ReadCodeViewDebugDirectoryData($entry).Path
                if ([IO.Path]::IsPathRooted($debugPath) -or $debugPath.Contains('/') -or $debugPath.Contains('\') -or $debugPath.Contains(':')) {
                    throw ('A managed binary contains a non-portable debug path: ' + [IO.Path]::GetFileName($path))
                }
            }
        } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}
Write-Output 'Managed binaries contain no absolute or directory-bearing CodeView paths.'
