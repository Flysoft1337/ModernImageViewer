#Requires -Version 7.0
param([string]$ArchivePath)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'The RAW preview bridge requires Windows x64 and MSVC.' }
$repository = Split-Path $PSScriptRoot -Parent
$output = Join-Path $repository 'artifacts/raw-native'
[IO.Directory]::CreateDirectory($output) | Out-Null
$archiveHash = 'AC64FA12BB00A7581332D4C6AB918C0533FB3F119D6B668D47A6875410DCA948'
$sourceUrl = 'https://www.libraw.org/data/LibRaw-0.22.2-Win64.zip'
if (-not $ArchivePath) {
    $ArchivePath = Join-Path $output 'LibRaw-0.22.2-Win64.zip'
    if (-not (Test-Path -LiteralPath $ArchivePath -PathType Leaf)) {
        Invoke-WebRequest -Uri $sourceUrl -OutFile $ArchivePath -TimeoutSec 60
    }
}
$ArchivePath = (Resolve-Path -LiteralPath $ArchivePath).Path
if ((Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash -ne $archiveHash) {
    throw 'The fixed official LibRaw archive SHA-256 does not match.'
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
$installation = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $installation) { throw 'MSVC x64 build tools are required to build the RAW preview bridge.' }
& (Join-Path $installation 'Common7/Tools/Launch-VsDevShell.ps1') -SkipAutomaticLocation -Arch amd64 -HostArch amd64 | Out-Null
$compiler = (Get-Command cl.exe -CommandType Application).Source
$compilerVersion = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
$flags = '/EHsc /utf-8 /MP /MT /I. /DWIN32 /O2 /W0 /nologo /DLIBRAW_MAX_ALLOC_MB_DEFAULT=64 /DLIBRAW_MAX_THUMBNAIL_MB=32 /DLIBRAW_MAX_PROFILE_SIZE_MB=8 /DLIBRAW_X3F_ALLOC_LIMIT_MB=32 /DLIBRAW_MAX_NONDNG_RAW_FILE_SIZE=268435456LL /DLIBRAW_MAX_DNG_RAW_FILE_SIZE=268435456LL'
$configuration = "$archiveHash|$compilerVersion|$flags"
$workKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($configuration))).Substring(0, 16)
$work = Join-Path $output "work/$workKey"
$vendor = Join-Path $work 'LibRaw-0.22.2'
if (-not (Test-Path (Join-Path $vendor 'libraw/libraw.h') -PathType Leaf)) {
    Expand-Archive -LiteralPath $ArchivePath -DestinationPath $work -Force
}
$nativeSource = Join-Path $repository 'src/ModernImageViewer.Codecs/Raw/Native/raw-preview.cpp'
$bridgeHash = (Get-FileHash -LiteralPath $nativeSource -Algorithm SHA256).Hash
$dll = Join-Path $output 'ModernImageViewer.RawBridge.dll'
$manifestPath = Join-Path $output 'raw-native.json'
if ((Test-Path $manifestPath -PathType Leaf) -and (Test-Path $dll -PathType Leaf)) {
    $previous = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($previous.Configuration -eq $configuration -and $previous.BridgeSourceSha256 -eq $bridgeHash -and
        $previous.NativeSha256 -eq (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash) {
        Write-Output 'RAW native bridge already matches the fixed source and compiler configuration.'
        Copy-Item -LiteralPath $ArchivePath -Destination (Join-Path $output 'licenses/LibRaw-0.22.2-source.zip') -Force
        return
    }
}
Push-Location $vendor
try {
    $staticLibrary = Join-Path $vendor 'lib/libraw_static.lib'
    $staticProof = Join-Path $vendor 'miv-static-build.json'
    $reuseStatic = $false
    if ((Test-Path $staticProof -PathType Leaf) -and (Test-Path $staticLibrary -PathType Leaf)) {
        $proof = Get-Content -LiteralPath $staticProof -Raw | ConvertFrom-Json
        $reuseStatic = $proof.Configuration -eq $configuration -and $proof.Sha256 -eq (Get-FileHash $staticLibrary -Algorithm SHA256).Hash
    }
    if (-not $reuseStatic) {
        # /A is intentional: the verified archive also ships a /MD static library.
        # Only our explicit build receipt + content hash can reuse the /MT library, never timestamps.
        & nmake.exe /nologo /A /f Makefile.msvc "COPT=$flags" 'lib\libraw_static.lib' *> (Join-Path $output 'libraw-build.log')
        if ($LASTEXITCODE -ne 0) { throw "LibRaw /MT build failed; see $(Join-Path $output 'libraw-build.log')" }
        [pscustomobject]@{ Configuration = $configuration; Sha256 = (Get-FileHash $staticLibrary -Algorithm SHA256).Hash } |
            ConvertTo-Json | Set-Content -LiteralPath $staticProof -Encoding utf8
    }
    & cl.exe /nologo /utf-8 /EHsc /MT /O2 /W4 /LD /DLIBRAW_NODLL /I. $nativeSource 'lib/libraw_static.lib' ws2_32.lib `
        "/Fo$(Join-Path $output 'raw-preview.obj')" /link "/OUT:$dll" /OPT:REF /OPT:ICF *> (Join-Path $output 'bridge-build.log')
    if ($LASTEXITCODE -ne 0) { throw "RAW bridge build failed; see $(Join-Path $output 'bridge-build.log')" }
}
finally { Pop-Location }
$dependents = & dumpbin.exe /nologo /dependents $dll
if ($LASTEXITCODE -ne 0) { throw 'Could not inspect RAW native dependencies.' }
$dependents | Set-Content (Join-Path $output 'raw-native-dependents.txt')
$imports = @($dependents | ForEach-Object { if ($_ -match '^\s+([A-Za-z0-9_.-]+\.dll)\s*$') { $matches[1] } })
foreach ($dependency in $imports) {
    if ($dependency -notin @('KERNEL32.dll', 'WS2_32.dll', 'ADVAPI32.dll', 'USER32.dll')) {
        throw "Unexpected RAW runtime dependency: $dependency. Do not depend on a user-installed runtime."
    }
}
$notices = Join-Path $output 'licenses'
[IO.Directory]::CreateDirectory($notices) | Out-Null
foreach ($name in @('COPYRIGHT', 'LICENSE.CDDL', 'LICENSE.LGPL')) {
    Copy-Item -LiteralPath (Join-Path $vendor $name) -Destination (Join-Path $notices $name) -Force
}
Copy-Item -LiteralPath $ArchivePath -Destination (Join-Path $notices 'LibRaw-0.22.2-source.zip') -Force
[pscustomobject]@{
    SchemaVersion = 1
    Version = 'LibRaw 0.22.2; preview ABI 1'
    SourceUrl = $sourceUrl
    ArchiveSha256 = $archiveHash
    BridgeSourceSha256 = $bridgeHash
    Configuration = $configuration
    CompilerVersion = $compilerVersion
    NativeSha256 = (Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash
    Dependencies = $imports
    License = 'LibRaw: CDDL-1.0 (LGPL-2.1 alternative also retained); bridge: repository MIT'
    Scope = 'Preview-only exported ABI; /MT build, no RawSpeed/DNG SDK/libjpeg/LCMS/OpenMP; limits are not a whole-process memory cap'
} | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Output "RAW native bridge built: $dll"
