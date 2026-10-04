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
    & dotnet publish (Join-Path $repository "src/ModernImageViewer.App/ModernImageViewer.App.csproj") `
        --configuration Release --runtime win-x64 --self-contained true --output $PublishDirectory `
        -p:PublishSingleFile=false -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) { throw "Windows x64 publish failed: $LASTEXITCODE" }
}

$appPath = Join-Path $PublishDirectory "ModernImageViewer.App.exe"
$runtimePath = Join-Path $PublishDirectory "ModernImageViewer.App.runtimeconfig.json"
if (-not (Test-Path $appPath -PathType Leaf) -or -not (Test-Path $runtimePath -PathType Leaf)) {
    throw "A complete win-x64 publish is required in $PublishDirectory."
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
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($library in $assets.libraries.PSObject.Properties) {
    if ($library.Value.type -ne "package") { continue }
    foreach ($packageRoot in $packageRoots) {
        $packageDirectory = Join-Path $packageRoot $library.Value.path
        if (-not (Test-Path $packageDirectory -PathType Container)) { continue }
        $notices = @(Get-ChildItem $packageDirectory -File | Where-Object {
            $_.Name -match '^(LICENSE(\.txt)?|THIRD-PARTY-NOTICES\.txt|ThirdPartyNotices\.txt)$'
        })
        if ($notices.Count -gt 0) {
            $noticeDirectory = Join-Path $PublishDirectory ("licenses/" + $library.Name.Replace("/", "-"))
            [IO.Directory]::CreateDirectory($noticeDirectory) | Out-Null
            foreach ($notice in $notices) { Copy-Item $notice.FullName (Join-Path $noticeDirectory $notice.Name) -Force }
        }
        break
    }
}
$skiaNotices = Get-ChildItem (Join-Path $PublishDirectory "licenses") -Directory -Filter "SkiaSharp.NativeAssets.Win32-*" |
    Where-Object { (Test-Path (Join-Path $_.FullName "LICENSE.txt")) -and (Test-Path (Join-Path $_.FullName "THIRD-PARTY-NOTICES.txt")) }
if (-not $skiaNotices) { throw "SkiaSharp native LICENSE.txt and THIRD-PARTY-NOTICES.txt must be included in the distribution." }

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
        [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file, $relativePath,
            [IO.Compression.CompressionLevel]::Optimal) | Out-Null
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
