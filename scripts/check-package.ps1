#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$PortableArchive,
    [string]$OutputPath = (Join-Path $PSScriptRoot '../artifacts/packaging/package-results.json')
)

$ErrorActionPreference = 'Stop'
$archive = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $PortableArchive).Path)
function Read-Entry([string]$Name) {
    $entry = $archive.GetEntry($Name)
    if ($null -eq $entry) { throw "Required package entry missing: $Name" }
    $reader = [IO.StreamReader]::new($entry.Open())
    try { return $reader.ReadToEnd() }
    finally { $reader.Dispose() }
}
try {
    $files = @($archive.Entries | Where-Object { -not $_.FullName.EndsWith('/') })
    $names = @($files.FullName)
    if (@($names | Select-Object -Unique).Count -ne $names.Count) { throw 'Duplicate package entries.' }
    foreach ($name in $names) {
        if ($name -notmatch '^(app/[^/]+|licenses/.+|LICENSE\.txt|dependencies\.json)$' -or
            $name -match '(\.pdb$|\.\.|install\.json$|app/ModernImageViewer\.App\.dll$)') {
            throw "Unexpected package layout: $name"
        }
    }
    if ($names -notcontains 'app/ModernImageViewer.App.exe') { throw 'Missing application bundle.' }
    if (@($names | Where-Object { $_ -like 'app/*' }).Count -gt 20) { throw 'Unexpected loose runtime files.' }
    $runtime = (Read-Entry 'app/ModernImageViewer.App.runtimeconfig.json' | ConvertFrom-Json).runtimeOptions
    if (-not $runtime.includedFrameworks -or $runtime.framework -or $runtime.frameworks) { throw 'Package is not self-contained.' }
    $inventory = Read-Entry 'dependencies.json' | ConvertFrom-Json
    if ($names -contains 'app/glfw3.dll' -or
        $names -match '^app/(OpenTK[.]|GLWpfControl[.]|Microsoft[.]Windows[.]SDK[.]NET[.])' -or
        $inventory.Packages.Package -match '^(OpenTK([./]|$)|GLWpfControl/)') {
        throw 'The package contains unused OpenGL or Windows SDK projection dependencies.'
    }
    foreach ($native in $inventory.NativeFiles) {
        $entry = $archive.GetEntry($native.File)
        if ($null -eq $entry -or $entry.Length -ne $native.Bytes) { throw 'Native inventory does not match package layout.' }
        $stream = $entry.Open()
        try { $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)) }
        finally { $stream.Dispose() }
        if ($hash -ine $native.Sha256) { throw 'Native package checksum mismatch.' }
    }
    $destination = [IO.Path]::GetFullPath($OutputPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [pscustomobject]@{
        Files = $files.Count
        RootFiles = @($names | Where-Object { -not $_.Contains('/') }).Count
        AppFiles = @($names | Where-Object { $_ -like 'app/*' }).Count
        PayloadBytes = ($files | Measure-Object -Property Length -Sum).Sum
        PortableBytes = (Get-Item -LiteralPath $PortableArchive).Length
        NativeFilesVerified = @($inventory.NativeFiles).Count
        Layout = 'app/ contains the compressed managed bundle and external native runtime; licenses/ contains notices and required sources.'
        RuntimeExtraction = $false
        Limitations = 'Payload excludes the installer marker and Inno uninstaller; installed bytes are measured separately by the installer smoke.'
    } | ConvertTo-Json | Set-Content -LiteralPath $destination -Encoding utf8
    Write-Output "Package layout and native hashes verified: $($files.Count) files."
}
finally { $archive.Dispose() }
