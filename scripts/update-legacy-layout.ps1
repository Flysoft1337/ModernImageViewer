#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string[]]$PortableArchive,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../packaging/windows/legacy-layout.sha256')
)

$ErrorActionPreference = 'Stop'
$entries = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($path in $PortableArchive) {
    $archivePath = (Resolve-Path -LiteralPath $path).Path
    $checksum = [regex]::Match((Get-Content -LiteralPath "$archivePath.sha256" -Raw), '\A([0-9a-f]{64})  ([^\r\n]+)\r?\n?\z')
    if (-not $checksum.Success -or $checksum.Groups[2].Value -cne [IO.Path]::GetFileName($archivePath) -or
        $checksum.Groups[1].Value -cne (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash.ToLowerInvariant()) {
        throw 'Legacy archive checksum must match the exact package.'
    }
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        foreach ($entry in $archive.Entries) {
            $relative = $entry.FullName.Replace('\', '/')
            # These files retain their location in the new layout.
            if ($relative.EndsWith('/') -or $relative.StartsWith('licenses/') -or
                $relative -in @('LICENSE.txt', 'dependencies.json') -or $relative.EndsWith('.pdb')) { continue }
            if ($relative.StartsWith('app/') -or $relative -match '(^/|\.\.|:|[\r\n])') {
                throw 'Only the known flat legacy layout can be inventoried.'
            }
            $stream = $entry.Open()
            try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() }
            finally { $stream.Dispose() }
            [void]$entries.Add("$hash  $relative")
        }
    }
    finally { $archive.Dispose() }
}
$marker = Join-Path $PSScriptRoot '../packaging/windows/ModernImageViewer.install.json'
$markerHash = (Get-FileHash -LiteralPath $marker -Algorithm SHA256).Hash.ToLowerInvariant()
[void]$entries.Add("$markerHash  ModernImageViewer.install.json")
[IO.File]::WriteAllLines([IO.Path]::GetFullPath($OutputPath), @($entries | Sort-Object -CaseSensitive), [Text.UTF8Encoding]::new($false))
Write-Output "Inventoried $($entries.Count) known legacy file hashes."
