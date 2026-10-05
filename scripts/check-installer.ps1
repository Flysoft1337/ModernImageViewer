#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$FixtureDirectory,
    [string]$LogDirectory = (Join-Path $PSScriptRoot "../artifacts/installer-smoke")
)

$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "Installer verification requires Windows." }
$installer = (Resolve-Path $InstallerPath).Path
$logs = [IO.Path]::GetFullPath($LogDirectory)
$directory = Join-Path ([IO.Path]::GetTempPath()) ("ModernImageViewer 安装验证 " + [Guid]::NewGuid().ToString("N"))
$executable = Join-Path $directory "ModernImageViewer.App.exe"
$uninstaller = Join-Path $directory "unins000.exe"
$applicationKey = "Software\ModernImageViewer\Installed"
$progId = "ModernImageViewer.Installed.Image"
$progIdKey = "Software\Classes\$progId"
$registeredApplicationsKey = "Software\RegisteredApplications"
$extensions = @(".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp", ".hdp", ".svg", ".avif", ".heif", ".heic", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".rw2", ".orf", ".pef")
$foreignCandidate = "ModernImageViewer.InstallerSmoke." + [Guid]::NewGuid().ToString("N")
$currentUser = [Microsoft.Win32.Registry]::CurrentUser
$candidateKeys = [Collections.Generic.List[string]]::new()
$uninstallCompleted = $false

function Assert-Equal($Actual, $Expected, [string]$Message) {
    if ($Actual -cne $Expected) { throw $Message }
}

function Read-RegistryValue([string]$Path, [string]$Name) {
    $key = $currentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $null }
    try {
        $value = $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
        if ($value -is [Array]) { return ,$value }
        return $value
    }
    finally { $key.Dispose() }
}

function Test-RegistryValue([string]$Path, [string]$Name) {
    $key = $currentUser.OpenSubKey($Path)
    if ($null -eq $key) { return $false }
    try { return $key.GetValueNames() -contains $Name }
    finally { $key.Dispose() }
}

function Read-OtherCandidates([string]$Path) {
    $key = $currentUser.OpenSubKey($Path)
    if ($null -eq $key) { return "[]" }
    try {
        $values = foreach ($name in ($key.GetValueNames() | Sort-Object)) {
            if ($name -ceq $progId -or $name -ceq $foreignCandidate) { continue }
            [ordered]@{
                Name = $name
                Kind = $key.GetValueKind($name).ToString()
                Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        }
        return (ConvertTo-Json -InputObject @($values) -Depth 20 -Compress)
    }
    finally { $key.Dispose() }
}

function Read-RegistrySnapshot([string]$Path) {
    $key = $currentUser.OpenSubKey($Path)
    if ($null -eq $key) { return "<missing>" }
    try {
        $values = foreach ($name in ($key.GetValueNames() | Sort-Object)) {
            [ordered]@{
                Name = $name
                Kind = $key.GetValueKind($name).ToString()
                Value = $key.GetValue($name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames)
            }
        }
        $children = foreach ($name in ($key.GetSubKeyNames() | Sort-Object)) {
            [ordered]@{ Name = $name; Snapshot = Read-RegistrySnapshot "$Path\$name" }
        }
        return (ConvertTo-Json -InputObject ([ordered]@{ Values = @($values); Children = @($children) }) -Depth 100 -Compress)
    }
    finally { $key.Dispose() }
}

function Invoke-Setup([string]$Path, [string[]]$SetupArguments) {
    $info = [Diagnostics.ProcessStartInfo]::new($Path)
    $info.UseShellExecute = $false
    foreach ($argument in $SetupArguments) { $info.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    try {
        if (-not $process.Start()) { throw "Installer process did not start." }
        if (-not $process.WaitForExit(120000)) {
            $process.Kill($true)
            $process.WaitForExit(5000) | Out-Null
            throw "Installer process exceeded the 120-second limit."
        }
        if ($process.ExitCode -ne 0) { throw "Installer process failed with exit code $($process.ExitCode)." }
    }
    finally { $process.Dispose() }
}

function Assert-Registration {
    Assert-Equal (Read-RegistryValue $applicationKey "Owner") "ModernImageViewer.Installed.v1" "Installed application ownership is missing."
    Assert-Equal (Read-RegistryValue $applicationKey "ExecutablePath") $executable "Installed application path is incorrect."
    Assert-Equal (Read-RegistryValue $progIdKey "Owner") "ModernImageViewer.Installed.v1" "Installed ProgID ownership is missing."
    Assert-Equal (Read-RegistryValue "$progIdKey\shell\open\command" "") ('"' + $executable + '" "%1"') "The installed open command must quote both paths."
    Assert-Equal (Read-RegistryValue "$progIdKey\DefaultIcon" "") ('"' + $executable + '",0') "The installed icon path is incorrect."
    Assert-Equal (Read-RegistryValue "$applicationKey\Capabilities" "ApplicationName") "Modern Image Viewer" "Application capabilities are missing."
    Assert-Equal (Read-RegistryValue "$applicationKey\Capabilities" "ApplicationDescription") "Browse supported images with Modern Image Viewer." "The capabilities description was not updated."
    Assert-Equal (Read-RegistryValue $registeredApplicationsKey "ModernImageViewer.Installed") "$applicationKey\Capabilities" "RegisteredApplications does not reference the installed capabilities."
    foreach ($extension in $extensions) {
        Assert-Equal (Read-RegistryValue "$applicationKey\Capabilities\FileAssociations" $extension) $progId "A supported format is missing from capabilities."
        $key = $currentUser.OpenSubKey("Software\Classes\$extension\OpenWithProgids")
        if ($null -eq $key) { throw "A supported format is missing its Open with candidate." }
        try {
            Assert-Equal ($key.GetValueKind($progId)) ([Microsoft.Win32.RegistryValueKind]::None) "Open with candidates must use REG_NONE."
            $value = $key.GetValue($progId)
            if ($value -isnot [byte[]] -or $value.Length -ne 0) { throw "Open with candidates must contain zero bytes." }
        }
        finally { $key.Dispose() }
    }
}

function Read-UninstallIdentity {
    $parent = $currentUser.OpenSubKey("Software\Microsoft\Windows\CurrentVersion\Uninstall")
    if ($null -eq $parent) { throw "The current-user uninstall entry is missing." }
    try {
        $matches = foreach ($name in $parent.GetSubKeyNames()) {
            $key = $parent.OpenSubKey($name)
            try {
                $location = [string]$key.GetValue("InstallLocation", "")
                if ($location.TrimEnd('\') -ieq $directory.TrimEnd('\')) { $name }
            }
            finally { $key.Dispose() }
        }
        if (@($matches).Count -ne 1) { throw "Installation must have exactly one uninstall entry." }
        Assert-Equal ([string]$matches) "{A2E8F5A0-6526-4C87-ADE1-04DE4A7F41C0}_is1" "The installer application identity changed."
        return [string]$matches
    }
    finally { $parent.Dispose() }
}

# Refuse to overwrite a real installed copy when this script is run outside CI.
foreach ($path in @($applicationKey, $progIdKey)) {
    $key = $currentUser.OpenSubKey($path)
    if ($null -ne $key) { $key.Dispose(); throw "An installed application identity already exists; use a disposable Windows account." }
}
if ($null -ne (Read-RegistryValue $registeredApplicationsKey "ModernImageViewer.Installed")) {
    throw "An installed application registration already exists; use a disposable Windows account."
}
foreach ($extension in $extensions) {
    if (Test-RegistryValue "Software\Classes\$extension\OpenWithProgids" $progId) {
        throw "An installed Open with candidate already exists; use a disposable Windows account."
    }
}

$protected = [ordered]@{}
$otherCandidates = [ordered]@{}
foreach ($extension in $extensions) {
    $protected["Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice"] = Read-RegistrySnapshot "Software\Microsoft\Windows\CurrentVersion\Explorer\FileExts\$extension\UserChoice"
    $protected["Software\Classes\$extension"] = Read-RegistryValue "Software\Classes\$extension" ""
    $otherCandidates[$extension] = Read-OtherCandidates "Software\Classes\$extension\OpenWithProgids"
}
foreach ($path in @("Software\ModernImageViewer\Portable", "Software\Classes\ModernImageViewer.Portable.Image")) {
    $protected[$path] = Read-RegistrySnapshot $path
}
$portableRegistration = Read-RegistryValue $registeredApplicationsKey "ModernImageViewer.Portable"

try {
    [IO.Directory]::CreateDirectory($logs) | Out-Null
    foreach ($extension in $extensions) {
        $path = "Software\Classes\$extension\OpenWithProgids"
        $key = $currentUser.CreateSubKey($path)
        try { $key.SetValue($foreignCandidate, [byte[]]::new(0), [Microsoft.Win32.RegistryValueKind]::None) }
        finally { $key.Dispose() }
        $candidateKeys.Add($path)
    }
    $common = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=$directory", "/TASKS=fileassoc")
    Invoke-Setup $installer ($common + @("/LOG=$(Join-Path $logs 'install.log')"))
    if (-not [IO.File]::Exists($executable)) { throw "The installed executable is missing." }
    if (@(Get-ChildItem $directory -Recurse -File -Filter "libSkiaSharp.dll").Count -ne 1) { throw "The native Skia library is missing or ambiguous." }
    $marker = Get-Content (Join-Path $directory "ModernImageViewer.install.json") -Raw | ConvertFrom-Json
    Assert-Equal $marker.distribution "installer" "The installed distribution marker is incorrect."
    Assert-Registration
    $uninstallIdentity = Read-UninstallIdentity
    & (Join-Path $PSScriptRoot "check-file-activation.ps1") -AppPath $executable -FixtureDirectory $FixtureDirectory

    $preferences = Join-Path $directory "custom-user-preferences.json"
    [IO.File]::WriteAllText($preferences, '{"keep":"user data"}')
    # Reproduce the previous JPEG/PNG-only registry state before repairing it via upgrade.
    $capabilities = $currentUser.OpenSubKey("$applicationKey\Capabilities", $true)
    try { $capabilities.SetValue("ApplicationDescription", "Browse JPEG and PNG images with Modern Image Viewer.") }
    finally { $capabilities.Dispose() }
    $formats = $currentUser.OpenSubKey("$applicationKey\Capabilities\FileAssociations", $true)
    try {
        foreach ($extension in ($extensions | Select-Object -Skip 3)) {
            $formats.DeleteValue($extension)
            $key = $currentUser.OpenSubKey("Software\Classes\$extension\OpenWithProgids", $true)
            try { $key.DeleteValue($progId) }
            finally { $key.Dispose() }
        }
    }
    finally { $formats.Dispose() }
    Invoke-Setup $installer ($common + @("/LOG=$(Join-Path $logs 'upgrade.log')"))
    Assert-Registration
    Assert-Equal (Read-UninstallIdentity) $uninstallIdentity "Upgrade created a different uninstall identity."
    Assert-Equal ([IO.File]::ReadAllText($preferences)) '{"keep":"user data"}' "Upgrade modified a custom user file."

    # An older description must also be removed; unrelated custom values remain protected.
    $capabilities = $currentUser.OpenSubKey("$applicationKey\Capabilities", $true)
    try { $capabilities.SetValue("ApplicationDescription", "Browse JPEG and PNG images with Modern Image Viewer.") }
    finally { $capabilities.Dispose() }
    Invoke-Setup $uninstaller @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$(Join-Path $logs 'uninstall.log')")
    $uninstallCompleted = $true
    if ([IO.File]::Exists($executable)) { throw "Uninstall left the application executable behind." }
    if ([IO.File]::Exists((Join-Path $directory "ModernImageViewer.install.json"))) { throw "Uninstall left the installed distribution marker behind." }
    Assert-Equal ([IO.File]::ReadAllText($preferences)) '{"keep":"user data"}' "Uninstall removed a custom user file."
    foreach ($path in @($applicationKey, $progIdKey, "Software\Microsoft\Windows\CurrentVersion\Uninstall\$uninstallIdentity")) {
        Assert-Equal (Read-RegistrySnapshot $path) "<missing>" "Uninstall left an owned application registry key behind."
    }
    Assert-Equal (Read-RegistryValue $registeredApplicationsKey "ModernImageViewer.Installed") $null "Uninstall left its RegisteredApplications value behind."
    foreach ($extension in $extensions) {
        $path = "Software\Classes\$extension\OpenWithProgids"
        if (Test-RegistryValue $path $progId) { throw "Uninstall left an owned Open with candidate behind." }
        Assert-Equal (Read-OtherCandidates $path) $otherCandidates[$extension] "Installation modified another existing Open with candidate."
        $key = $currentUser.OpenSubKey($path)
        try {
            Assert-Equal ($key.GetValueKind($foreignCandidate)) ([Microsoft.Win32.RegistryValueKind]::None) "Uninstall removed another Open with candidate."
        }
        finally { if ($null -ne $key) { $key.Dispose() } }
    }
    foreach ($path in $protected.Keys) {
        $actual = if ($path -match '^Software\\Classes\\\.(jpg|jpeg|png|bmp|gif|tif|tiff|ico|webp|jxr|wdp|hdp|svg|avif|heif|heic|dng|cr2|cr3|nef|arw|raf|rw2|orf|pef)$') { Read-RegistryValue $path "" } else { Read-RegistrySnapshot $path }
        Assert-Equal $actual $protected[$path] "Installation changed an existing default choice or portable identity."
    }
    Assert-Equal (Read-RegistryValue $registeredApplicationsKey "ModernImageViewer.Portable") $portableRegistration "Installation changed the portable RegisteredApplications value."
    Write-Output "Installer verification passed: current-user install, quoted file activation, same-version reinstall with legacy JPEG/PNG registry migration and clean uninstall; defaults, other candidates and user files were preserved."
}
finally {
    # Only run the uninstaller from our exclusively owned temporary directory.
    $cleanupSucceeded = $true
    if (-not $uninstallCompleted -and [IO.File]::Exists($uninstaller)) {
        try { Invoke-Setup $uninstaller @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/LOG=$(Join-Path $logs 'cleanup.log')") }
        catch {
            $cleanupSucceeded = $false
            Write-Warning "Installer cleanup failed; the temporary installation was retained at $directory. Inspect logs at $logs. $($_.Exception.Message)"
        }
    }
    foreach ($path in $candidateKeys) {
        $key = $currentUser.OpenSubKey($path, $true)
        if ($null -ne $key) {
            try { $key.DeleteValue($foreignCandidate, $false) }
            finally { $key.Dispose() }
        }
    }
    if ($cleanupSucceeded -and [IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory, $true) }
    Write-Output "Installer smoke-test logs: $logs"
}
