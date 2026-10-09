#Requires -Version 7.0
[CmdletBinding()]
param(
    [switch]$SkipPublish,
    [string]$PublishDirectory,
    [string]$OutputDirectory,
    [string]$CompilerPath
)

$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "The Windows installer must be built on Windows with PowerShell 7." }

$repository = Split-Path $PSScriptRoot -Parent
if (-not $PublishDirectory) { $PublishDirectory = Join-Path $repository "artifacts/publish/win-x64" }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $repository "artifacts/installer" }
$PublishDirectory = [IO.Path]::GetFullPath($PublishDirectory)
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
[xml]$properties = Get-Content (Join-Path $repository "Directory.Build.props") -Raw
$version = [string]$properties.Project.PropertyGroup.Version
if ($version -notmatch '^\d+\.\d+\.\d+(\.\d+)?$') { throw "Directory.Build.props must define a numeric installer Version." }

if (-not $SkipPublish) {
    if (Test-Path $PublishDirectory) {
        if (@(Get-ChildItem -LiteralPath $PublishDirectory -Force).Count -ne 0) {
            throw 'Choose an empty PublishDirectory to avoid carrying files from an older publish into the package.'
        }
    }
    & (Join-Path $PSScriptRoot "build-raw-native.ps1")
    & dotnet publish (Join-Path $repository "src/ModernImageViewer.App/ModernImageViewer.App.csproj") `
        --configuration Release --runtime win-x64 --self-contained true --output $PublishDirectory `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "Windows x64 publish failed: $LASTEXITCODE" }
}

$appPath = Join-Path $PublishDirectory "ModernImageViewer.App.exe"
$runtimePath = Join-Path $PublishDirectory "ModernImageViewer.App.runtimeconfig.json"
if (-not (Test-Path $appPath -PathType Leaf) -or -not (Test-Path $runtimePath -PathType Leaf)) {
    throw "A complete win-x64 publish is required in $PublishDirectory."
}
if (Test-Path (Join-Path $PublishDirectory 'ModernImageViewer.App.dll')) {
    throw 'The package requires the compressed single-file publish; use a fresh publish directory.'
}
$runtime = Get-Content $runtimePath -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks -or $runtime.runtimeOptions.framework -or $runtime.runtimeOptions.frameworks) {
    throw "The installer requires a self-contained publish; the user should not need to install .NET."
}
if (Test-Path (Join-Path $PublishDirectory "ModernImageViewer.install.json")) {
    throw "The publish directory must remain portable; the installer adds its distribution marker separately."
}

# Carry the repository license and available notices from the actual resolved packages.
# Keep any runtime license already supplied by dotnet publish before using LICENSE.txt for ours.
$repositoryLicense = Join-Path $repository "LICENSE"
$publishedLicense = Join-Path $PublishDirectory "LICENSE.txt"
if (Test-Path $publishedLicense -PathType Leaf) {
    if ((Get-FileHash $publishedLicense).Hash -ne (Get-FileHash $repositoryLicense).Hash) {
        $runtimeLicenses = Join-Path $PublishDirectory "licenses/dotnet-runtime-publish"
        [IO.Directory]::CreateDirectory($runtimeLicenses) | Out-Null
        Copy-Item $publishedLicense (Join-Path $runtimeLicenses "LICENSE.txt") -Force
    }
}
Copy-Item $repositoryLicense $publishedLicense -Force
$assetsPath = Join-Path $repository "src/ModernImageViewer.App/obj/project.assets.json"
if (-not (Test-Path $assetsPath -PathType Leaf)) { throw "Resolved project.assets.json is required to include dependency licenses." }
$assets = Get-Content $assetsPath -Raw | ConvertFrom-Json
if ($assets.libraries.PSObject.Properties.Name -match '^(OpenTK([./]|$)|GLWpfControl/)') {
    throw 'The CPU-only viewer must not resolve the optional OpenGL control dependencies.'
}
if (Test-Path (Join-Path $PublishDirectory 'glfw3.dll')) {
    throw 'The publish directory contains an unused OpenGL runtime; publish into a fresh directory.'
}
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $packId = switch ($framework.name) {
        'Microsoft.NETCore.App' { 'microsoft.netcore.app.runtime.win-x64' }
        'Microsoft.WindowsDesktop.App' { 'microsoft.windowsdesktop.app.runtime.win-x64' }
        default { throw 'Unexpected bundled runtime framework.' }
    }
    $pack = @($packageRoots | ForEach-Object { Join-Path $_ "$packId/$($framework.version)" } |
        Where-Object { Test-Path $_ -PathType Container }) | Select-Object -First 1
    if (-not $pack) { throw 'The exact bundled runtime pack is required to distribute its license.' }
    $runtimeNotices = @(Get-ChildItem -LiteralPath $pack -File | Where-Object { $_.Name -match '^(LICENSE(\.TXT)?|THIRD-PARTY-NOTICES\.TXT)$' })
    if (-not ($runtimeNotices.Name -match '^LICENSE')) { throw 'Bundled runtime license is missing.' }
    if ($framework.name -eq 'Microsoft.NETCore.App' -and -not ($runtimeNotices.Name -match '^THIRD-PARTY-NOTICES')) {
        throw 'Bundled .NET runtime third-party notices are missing.'
    }
    $destination = Join-Path $PublishDirectory "licenses/$packId-$($framework.version)"
    [IO.Directory]::CreateDirectory($destination) | Out-Null
    $runtimeNotices | Copy-Item -Destination $destination -Force
}
if ($assets.libraries.PSObject.Properties.Name -like 'Svg.Custom/*') {
    $sourceNotices = Join-Path $repository 'third_party/licenses'
    $destinationNotices = Join-Path $PublishDirectory 'licenses/svg-source-notices'
    [IO.Directory]::CreateDirectory($destinationNotices) | Out-Null
    foreach ($name in @('Svg.Skia-MIT.txt', 'Svg.Custom-MS-PL.txt', 'ExCSS-MIT.txt', 'THIRD-PARTY-NOTICES.txt', 'README.md')) {
        $sourceNotice = Join-Path $sourceNotices $name
        if (-not (Test-Path $sourceNotice -PathType Leaf)) { throw "Required SVG dependency notice is missing: $name" }
        Copy-Item -LiteralPath $sourceNotice -Destination (Join-Path $destinationNotices $name) -Force
    }
}
$modernPackage = $assets.libraries.PSObject.Properties.Name -like 'Magick.NET-Q8-x64/*'
if ($modernPackage) {
    $sourceNotices = Join-Path $repository 'third_party/licenses/modern'
    $destinationNotices = Join-Path $PublishDirectory 'licenses/modern-source-notices'
    [IO.Directory]::CreateDirectory($destinationNotices) | Out-Null
    foreach ($name in @('Apache-2.0.txt', 'Copyright.txt', 'README.md')) {
        $sourceNotice = Join-Path $sourceNotices $name
        if (-not (Test-Path $sourceNotice -PathType Leaf)) { throw "Required modern codec notice is missing: $name" }
        Copy-Item -LiteralPath $sourceNotice -Destination (Join-Path $destinationNotices $name) -Force
    }
}
$packageInventory = [Collections.Generic.List[object]]::new()
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne "package") { continue }
    foreach ($packageRoot in $packageRoots) {
        $packageDirectory = Join-Path $packageRoot $library.Value.path
        if (-not (Test-Path $packageDirectory -PathType Container)) { continue }
        $notices = @(Get-ChildItem -LiteralPath $packageDirectory -File -Recurse | Where-Object {
            $_.Name -match '^(LICEN[CS]E([._-].*)?|COPYING([._-].*)?|COPYRIGHT([._-].*)?|NOTICE([._-].*)?|THIRD[-_]?PARTY[-_]?NOTICES([._-].*)?)$'
        })
        if ($notices.Count -gt 0) {
            $noticeDirectory = Join-Path $PublishDirectory ("licenses/" + $library.Name.Replace("/", "-"))
            [IO.Directory]::CreateDirectory($noticeDirectory) | Out-Null
            foreach ($notice in $notices) {
                $relativeNotice = [IO.Path]::GetRelativePath($packageDirectory, $notice.FullName)
                $noticeDestination = Join-Path $noticeDirectory $relativeNotice
                [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($noticeDestination)) | Out-Null
                Copy-Item -LiteralPath $notice.FullName -Destination $noticeDestination -Force
            }
        }
        $specPath = Get-ChildItem -LiteralPath $packageDirectory -Filter '*.nuspec' -File | Select-Object -First 1
        $licenseIdentifier = $null
        $licenseKind = $null
        $sourceRepository = $null
        $sourceCommit = $null
        if ($specPath) {
            $settings = [Xml.XmlReaderSettings]::new()
            $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
            $settings.XmlResolver = $null
            $reader = [Xml.XmlReader]::Create($specPath.FullName, $settings)
            try {
                $spec = [Xml.XmlDocument]::new()
                $spec.XmlResolver = $null
                $spec.Load($reader)
                $metadata = $spec.GetElementsByTagName('metadata') | Select-Object -First 1
                $licenseIdentifier = $metadata.license.InnerText
                $licenseKind = $metadata.license.type
                $sourceRepository = $metadata.repository.url
                $sourceCommit = $metadata.repository.commit
            }
            finally { $reader.Dispose() }
        }
        $packageInventory.Add([pscustomobject]@{
            Package = $library.Name
            PackageSha512 = $library.Value.sha512
            LicenseKind = $licenseKind
            License = $licenseIdentifier
            Repository = $sourceRepository
            Commit = $sourceCommit
            NoticeFiles = @($notices | ForEach-Object { [IO.Path]::GetRelativePath($packageDirectory, $_.FullName).Replace('\', '/') })
        })
        break
    }
}
$skiaNotices = Get-ChildItem (Join-Path $PublishDirectory "licenses") -Directory -Filter "SkiaSharp.NativeAssets.Win32-*" |
    Where-Object { (Test-Path (Join-Path $_.FullName "LICENSE.txt")) -and (Test-Path (Join-Path $_.FullName "THIRD-PARTY-NOTICES.txt")) }
if (-not $skiaNotices) { throw "SkiaSharp native LICENSE.txt and THIRD-PARTY-NOTICES.txt must be included in the distribution." }
if ($modernPackage) {
    $modernNotices = Get-ChildItem (Join-Path $PublishDirectory 'licenses') -Directory -Filter 'Magick.NET-Q8-x64-*' |
        Where-Object { Test-Path (Join-Path $_.FullName 'Notice.txt') -PathType Leaf }
    if (-not $modernNotices) { throw 'Magick.NET native Notice.txt must be included in the distribution.' }
    if (@(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File -Filter 'Magick.Native-Q8-x64.dll').Count -ne 1) {
        throw 'Exactly one bundled x64 Magick native library is required.'
    }
}
$rawBridge = Join-Path $PublishDirectory 'ModernImageViewer.RawBridge.dll'
$rawManifestPath = Join-Path $PublishDirectory 'raw-native.json'
if (-not (Test-Path $rawBridge -PathType Leaf) -or -not (Test-Path $rawManifestPath -PathType Leaf)) {
    throw 'Bundled RAW preview bridge and source manifest must be included in the distribution.'
}
$rawManifest = Get-Content -LiteralPath $rawManifestPath -Raw | ConvertFrom-Json
if ($rawManifest.NativeSha256 -ne (Get-FileHash -LiteralPath $rawBridge -Algorithm SHA256).Hash) {
    throw 'Published RAW bridge does not match its source manifest.'
}
$rawSourceArchive = Join-Path $PublishDirectory 'licenses/raw-native/LibRaw-0.22.2-source.zip'
if (-not (Test-Path $rawSourceArchive -PathType Leaf) -or
    (Get-FileHash -LiteralPath $rawSourceArchive -Algorithm SHA256).Hash -ne $rawManifest.ArchiveSha256) {
    throw 'The fixed LibRaw source archive must be distributed with its matching hash.'
}
foreach ($name in @('COPYRIGHT', 'LICENSE.CDDL', 'LICENSE.LGPL')) {
    if (-not (Test-Path (Join-Path $PublishDirectory "licenses/raw-native/$name") -PathType Leaf)) {
        throw "Required RAW native license is missing: $name"
    }
}
$rawNotice = Join-Path $repository 'third_party/licenses/raw/README.md'
if (-not (Test-Path $rawNotice -PathType Leaf)) { throw 'RAW native source/build notice is missing.' }
Copy-Item -LiteralPath $rawNotice -Destination (Join-Path $PublishDirectory 'licenses/raw-native/README.md') -Force
$nativeInventory = @(Get-ChildItem -LiteralPath $PublishDirectory -Recurse -File | Where-Object {
    $_.Name -match '^(libSkiaSharp|libHarfBuzzSharp|Magick\.Native|ModernImageViewer\.RawBridge).*\.dll$'
} | ForEach-Object {
    [pscustomobject]@{
        File = 'app/' + [IO.Path]::GetRelativePath($PublishDirectory, $_.FullName).Replace('\', '/')
        Bytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
    }
})
[pscustomobject]@{
    SchemaVersion = 1
    Scope = 'Resolved NuGet declarations, fixed native source manifests and actual published image native files; not a certification or complete OS/.NET SBOM'
    Packages = $packageInventory
    RuntimeFrameworks = @($runtime.runtimeOptions.includedFrameworks)
    NativeFiles = $nativeInventory
    NativeSources = @($rawManifest)
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $PublishDirectory 'dependencies.json') -Encoding utf8

# Pin both the official compiler release and its GitHub asset checksum.
$compilerVersion = "7.1.0"
$compilerSha256 = "0362a383ed217d4c4239b5933866dd96d3eb2102737da92f80f6057a4b40df2f"
$compilerUrl = "https://github.com/jrsoftware/issrc/releases/download/is-7_1_0/innosetup-7.1.0-x64.exe"
function Test-CompilerVersion([string]$Path) {
    if (-not $Path -or -not (Test-Path $Path -PathType Leaf)) { return $false }
    # ISCC's PE version resource does not identify the loaded compiler engine.
    # Inno Setup 7 exposes the authoritative engine version through --version.
    try {
        $reportedVersion = (& $Path --version 2>&1) -join "`n"
        $succeeded = $LASTEXITCODE -eq 0 -and $reportedVersion.Trim() -eq $compilerVersion
        if (-not $succeeded) { Write-Host "Compiler version check failed at ${Path}: $reportedVersion" }
        return $succeeded
    }
    catch {
        Write-Host "Compiler could not report its engine version at ${Path}: $($_.Exception.Message)"
        return $false
    }
}

if ($CompilerPath) {
    $CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
    if (-not (Test-CompilerVersion $CompilerPath)) { throw "CompilerPath must point to Inno Setup $compilerVersion ISCC.exe." }
}
else {
    $cacheRoot = if ($env:RUNNER_TEMP) { $env:RUNNER_TEMP } else { [IO.Path]::GetTempPath() }
    $compilerDirectory = Join-Path $cacheRoot "ModernImageViewer-InnoSetup-$compilerVersion"
    $CompilerPath = Join-Path $compilerDirectory "ISCC.exe"
    if (-not (Test-CompilerVersion $CompilerPath)) {
        $compilerInstaller = Join-Path $cacheRoot "ModernImageViewer-InnoSetup-$compilerVersion-x64.exe"
        if (-not (Test-Path $compilerInstaller -PathType Leaf) -or
            (Get-FileHash $compilerInstaller -Algorithm SHA256).Hash -ne $compilerSha256) {
            Invoke-WebRequest -Uri $compilerUrl -OutFile $compilerInstaller
        }
        if ((Get-FileHash $compilerInstaller -Algorithm SHA256).Hash -ne $compilerSha256) {
            throw "The downloaded Inno Setup compiler checksum does not match the pinned official asset."
        }
        $info = [Diagnostics.ProcessStartInfo]::new($compilerInstaller)
        $info.UseShellExecute = $false
        foreach ($argument in @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/CURRENTUSER", "/DIR=$compilerDirectory")) {
            $info.ArgumentList.Add($argument)
        }
        $process = [Diagnostics.Process]::Start($info)
        try {
            if (-not $process.WaitForExit(120000)) { $process.Kill($true); throw "Inno Setup compiler installation timed out." }
            if ($process.ExitCode -ne 0) { throw "Inno Setup compiler installation failed: $($process.ExitCode)" }
        }
        finally { $process.Dispose() }
        if (-not (Test-CompilerVersion $CompilerPath)) {
            if (Test-Path $compilerDirectory) {
                Write-Host "Compiler installation directory entries: $((Get-ChildItem $compilerDirectory -Name) -join ', ')"
            }
            throw "The pinned Inno Setup $compilerVersion engine was not installed successfully at $CompilerPath."
        }
    }
}

[IO.Directory]::CreateDirectory($OutputDirectory) | Out-Null
$expectedInstaller = Join-Path $OutputDirectory "ModernImageViewer-$version-win-x64-Setup.exe"
if (Test-Path $expectedInstaller) { Remove-Item $expectedInstaller }
& $CompilerPath "/DAppVersion=$version" "/DPublishDirectory=$PublishDirectory" "/DInstallerOutputDirectory=$OutputDirectory" `
    (Join-Path $repository "packaging/windows/ModernImageViewer.iss")
if ($LASTEXITCODE -ne 0 -or -not (Test-Path $expectedInstaller -PathType Leaf)) {
    throw "Installer compilation failed: $LASTEXITCODE"
}
$hash = (Get-FileHash $expectedInstaller -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$expectedInstaller.sha256", "$hash  $([IO.Path]::GetFileName($expectedInstaller))`n", [Text.UTF8Encoding]::new($false))
$portableArchive = Join-Path $OutputDirectory "ModernImageViewer-$version-win-x64-Portable.zip"
if (Test-Path $portableArchive) { Remove-Item $portableArchive }
$archive = [IO.Compression.ZipFile]::Open($portableArchive, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in [IO.Directory]::EnumerateFiles($PublishDirectory, "*", [IO.SearchOption]::AllDirectories)) {
        # Match the installer: debugger symbols are unnecessary for running the portable application.
        if ([IO.Path]::GetExtension($file) -ieq ".pdb") { continue }
        $relativePath = [IO.Path]::GetRelativePath($PublishDirectory, $file).Replace('\', '/')
        if ($relativePath -notlike 'licenses/*' -and $relativePath -notin @('LICENSE.txt', 'dependencies.json')) {
            $relativePath = 'app/' + $relativePath
        }
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $relativePath,
            [IO.Compression.CompressionLevel]::SmallestSize) | Out-Null
    }
}
finally { $archive.Dispose() }
$portableHash = (Get-FileHash $portableArchive -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$portableArchive.sha256", "$portableHash  $([IO.Path]::GetFileName($portableArchive))`n", [Text.UTF8Encoding]::new($false))
Write-Output "Installer: $expectedInstaller"
Write-Output "SHA256: $hash"
Write-Output "Portable archive: $portableArchive"
Write-Output "Portable SHA256: $portableHash"
Write-Output "The installer is unsigned. Install only packages obtained from the project or your own build."
