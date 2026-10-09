#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$BaselineInstallerPath,
    [string]$FixtureDirectory,
    [string]$LogDirectory = (Join-Path $PSScriptRoot "../artifacts/installer-smoke"),
    [ValidateSet('0.4.0', '0.5.0')][string]$BaselineVersion = '0.5.0'
)

$ErrorActionPreference = "Stop"
if (-not $IsWindows) { throw "Installer verification requires Windows." }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer verification requires a disposable GitHub-hosted Windows runner.'
}
$installer = (Resolve-Path $InstallerPath).Path
$baselineInstaller = if ($BaselineInstallerPath) { (Resolve-Path -LiteralPath $BaselineInstallerPath).Path } else { $null }
if ($baselineInstaller -and (Get-FileHash -LiteralPath $baselineInstaller).Hash -eq (Get-FileHash -LiteralPath $installer).Hash) {
    throw 'Baseline and target installers must be different packages.'
}
$logs = [IO.Path]::GetFullPath($LogDirectory)
$directory = Join-Path ([IO.Path]::GetTempPath()) ("ModernImageViewer 安装验证 " + [Guid]::NewGuid().ToString("N"))
$executable = Join-Path $directory "ModernImageViewer.App.exe"
$uninstaller = Join-Path $directory "unins000.exe"
$applicationKey = "Software\ModernImageViewer\Installed"
$progId = "ModernImageViewer.Installed.Image"
$progIdKey = "Software\Classes\$progId"
$registeredApplicationsKey = "Software\RegisteredApplications"
$extensions = @(".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tif", ".tiff", ".ico", ".webp", ".jxr", ".wdp", ".hdp", ".svg", ".avif", ".heif", ".heic", ".dng", ".cr2", ".cr3", ".nef", ".arw", ".raf", ".rw2", ".orf", ".pef")
$baselineExtensions = if ($BaselineVersion -eq '0.4.0') {
    @('.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff', '.ico', '.webp')
} else { $extensions }
$portableProgId = 'ModernImageViewer.Portable.Image'
$foreignCandidate = "ModernImageViewer.InstallerSmoke." + [Guid]::NewGuid().ToString("N")
$currentUser = [Microsoft.Win32.Registry]::CurrentUser
$candidateKeys = [Collections.Generic.List[string]]::new()
$portableCandidateKeys = [Collections.Generic.List[string]]::new()
$uninstallCompleted = $false
$lifecycle = [Collections.Generic.List[object]]::new()
$verificationSucceeded = $false

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

function Assert-Registration([string[]]$ExpectedExtensions = $extensions) {
    Assert-Equal (Read-RegistryValue $applicationKey "Owner") "ModernImageViewer.Installed.v1" "Installed application ownership is missing."
    Assert-Equal (Read-RegistryValue $applicationKey "ExecutablePath") $executable "Installed application path is incorrect."
    Assert-Equal (Read-RegistryValue $progIdKey "Owner") "ModernImageViewer.Installed.v1" "Installed ProgID ownership is missing."
    Assert-Equal (Read-RegistryValue "$progIdKey\shell\open\command" "") ('"' + $executable + '" "%1"') "The installed open command must quote both paths."
    Assert-Equal (Read-RegistryValue "$progIdKey\DefaultIcon" "") ('"' + $executable + '",0') "The installed icon path is incorrect."
    Assert-Equal (Read-RegistryValue "$applicationKey\Capabilities" "ApplicationName") "Modern Image Viewer" "Application capabilities are missing."
    Assert-Equal (Read-RegistryValue "$applicationKey\Capabilities" "ApplicationDescription") "Browse supported images with Modern Image Viewer." "The capabilities description was not updated."
    Assert-Equal (Read-RegistryValue $registeredApplicationsKey "ModernImageViewer.Installed") "$applicationKey\Capabilities" "RegisteredApplications does not reference the installed capabilities."
    $associations = $currentUser.OpenSubKey("$applicationKey\Capabilities\FileAssociations")
    if ($null -eq $associations) { throw 'Installed file associations are missing.' }
    try { Assert-Equal ($associations.GetValueNames().Count) $ExpectedExtensions.Count 'The installed association count does not match its version.' }
    finally { $associations.Dispose() }
    foreach ($extension in $ExpectedExtensions) {
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
    foreach ($extension in $extensions) {
        if ($extension -notin $ExpectedExtensions -and (Test-RegistryValue "Software\Classes\$extension\OpenWithProgids" $progId)) {
            throw 'The baseline registered a format it does not support.'
        }
    }
}

function Assert-ProtectedState {
    foreach ($extension in $extensions) {
        $path = "Software\Classes\$extension\OpenWithProgids"
        Assert-Equal (Read-OtherCandidates $path) $otherCandidates[$extension] 'Installation modified another existing Open with candidate.'
        $key = $currentUser.OpenSubKey($path)
        if ($null -eq $key) { throw 'Installation removed other Open with candidates.' }
        try {
            Assert-Equal ($key.GetValueKind($foreignCandidate)) ([Microsoft.Win32.RegistryValueKind]::None) 'Installation removed another Open with candidate.'
            $value = $key.GetValue($foreignCandidate)
            if ($value -isnot [byte[]] -or $value.Length -ne 0) { throw 'Installation changed another Open with candidate.' }
        }
        finally { $key.Dispose() }
    }
    foreach ($path in $protected.Keys) {
        $actual = if ($path -match '^Software\\Classes\\\.(jpg|jpeg|png|bmp|gif|tif|tiff|ico|webp|jxr|wdp|hdp|svg|avif|heif|heic|dng|cr2|cr3|nef|arw|raf|rw2|orf|pef)$') { Read-RegistryValue $path "" } else { Read-RegistrySnapshot $path }
        Assert-Equal $actual $protected[$path] 'Installation changed an existing default choice or portable identity.'
    }
    Assert-Equal (Read-RegistryValue $registeredApplicationsKey 'ModernImageViewer.Portable') $portableRegistration 'Installation changed the portable RegisteredApplications value.'
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

function Read-InstalledVersion([string]$Stage) {
    $identity = Read-UninstallIdentity
    $displayVersion = [string](Read-RegistryValue "Software\Microsoft\Windows\CurrentVersion\Uninstall\$identity" 'DisplayVersion')
    $assemblyPath = Join-Path $directory 'ModernImageViewer.App.dll'
    if (-not [IO.File]::Exists($assemblyPath)) { throw 'The installed application assembly is missing.' }
    $versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assemblyPath)
    $fileVersion = [version]::new($versionInfo.FileMajorPart, $versionInfo.FileMinorPart, $versionInfo.FileBuildPart)
    $registeredVersion = [version]$displayVersion
    if ($registeredVersion.Major -ne $fileVersion.Major -or $registeredVersion.Minor -ne $fileVersion.Minor -or
        $registeredVersion.Build -ne $fileVersion.Build) { throw 'Installed assembly and uninstall DisplayVersion disagree.' }
    $lifecycle.Add([pscustomobject]@{
        Stage = $Stage
        FileVersion = $fileVersion.ToString()
        DisplayVersion = $displayVersion
        AssemblySha256 = (Get-FileHash -LiteralPath $assemblyPath -Algorithm SHA256).Hash
        UninstallIdentity = $identity
    })
    return $fileVersion
}

function Assert-InstalledPayload([version]$Version) {
    if (-not [IO.File]::Exists($executable)) { throw 'The installed executable is missing.' }
    $nativeFiles = @('libSkiaSharp.dll')
    if ($Version -ge [version]'0.5.0') { $nativeFiles += @('Magick.Native-Q8-x64.dll', 'ModernImageViewer.RawBridge.dll') }
    foreach ($native in $nativeFiles) {
        if (@(Get-ChildItem -LiteralPath $directory -Recurse -File -Filter $native).Count -ne 1) {
            throw 'A required installed native dependency is missing or ambiguous.'
        }
    }
    $marker = Get-Content (Join-Path $directory 'ModernImageViewer.install.json') -Raw | ConvertFrom-Json
    Assert-Equal $marker.distribution 'installer' 'The installed distribution marker is incorrect.'
}

# The disposable runner must also start without an installed identity.
foreach ($path in @($applicationKey, $progIdKey, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{A2E8F5A0-6526-4C87-ADE1-04DE4A7F41C0}_is1')) {
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
        try {
            $key.SetValue($foreignCandidate, [byte[]]::new(0), [Microsoft.Win32.RegistryValueKind]::None)
            $candidateKeys.Add($path)
            # Seed a portable candidate so preservation is exercised on a clean VM.
            if ($key.GetValueNames() -notcontains $portableProgId) {
                $key.SetValue($portableProgId, [byte[]]::new(0), [Microsoft.Win32.RegistryValueKind]::None)
                $portableCandidateKeys.Add($path)
            }
        }
        finally { $key.Dispose() }
        $otherCandidates[$extension] = Read-OtherCandidates $path
    }
    $common = @("/VERYSILENT", "/SUPPRESSMSGBOXES", "/NORESTART", "/SP-", "/DIR=$directory", "/TASKS=fileassoc")
    $firstInstaller = if ($baselineInstaller) { $baselineInstaller } else { $installer }
    $firstLog = if ($baselineInstaller) { 'baseline-install.log' } else { 'install.log' }
    Invoke-Setup $firstInstaller ($common + @("/LOG=$(Join-Path $logs $firstLog)"))
    $uninstallIdentity = Read-UninstallIdentity
    $initialVersion = Read-InstalledVersion $(if ($baselineInstaller) { 'BaselineInstall' } else { 'Install' })
    if ($baselineInstaller -and $initialVersion -ne [version]$BaselineVersion) { throw "The actual installed baseline must be version $BaselineVersion." }
    Assert-InstalledPayload $initialVersion
    Assert-Registration $(if ($baselineInstaller) { $baselineExtensions } else { $extensions })
    Assert-ProtectedState
    # 0.4 can open the generated PNG/WebP, but not the newer codec fixture groups.
    $initialFixtures = if ($baselineInstaller -and $BaselineVersion -eq '0.4.0') { $null } else { $FixtureDirectory }
    & (Join-Path $PSScriptRoot "check-file-activation.ps1") -AppPath $executable -FixtureDirectory $initialFixtures

    $preferences = Join-Path $directory "custom-user-preferences.json"
    [IO.File]::WriteAllText($preferences, '{"keep":"user data"}')
    if ($baselineInstaller) {
        Invoke-Setup $installer ($common + @("/LOG=$(Join-Path $logs "upgrade-$BaselineVersion-to-0.6.0.log")"))
        $upgradedVersion = Read-InstalledVersion 'CrossVersionUpgrade'
        if ($upgradedVersion -ne [version]'0.6.0' -or $upgradedVersion -le $initialVersion) {
            throw "The actual cross-version upgrade must replace $BaselineVersion with 0.6.0."
        }
        Assert-InstalledPayload $upgradedVersion
        Assert-Registration
        Assert-ProtectedState
        if ($lifecycle[0].AssemblySha256 -eq $lifecycle[1].AssemblySha256) { throw 'Upgrade did not replace the application assembly.' }
        Assert-Equal (Read-UninstallIdentity) $uninstallIdentity 'Upgrade created a different uninstall identity.'
        Assert-Equal ([IO.File]::ReadAllText($preferences)) '{"keep":"user data"}' 'Upgrade modified a custom user file.'
        & (Join-Path $PSScriptRoot 'check-file-activation.ps1') -AppPath $executable -FixtureDirectory $FixtureDirectory
    }
    else { $upgradedVersion = $initialVersion }
    # Registry migration remains a separate same-version reinstall check.
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
    $beforeReinstallHash = $lifecycle[-1].AssemblySha256
    Invoke-Setup $installer ($common + @("/LOG=$(Join-Path $logs 'same-version-reinstall.log')"))
    Assert-InstalledPayload $upgradedVersion
    Assert-Registration
    Assert-ProtectedState
    Assert-Equal (Read-InstalledVersion 'SameVersionReinstall') $upgradedVersion 'Same-version reinstall changed the installed version.'
    Assert-Equal $lifecycle[-1].AssemblySha256 $beforeReinstallHash 'Same-version reinstall changed the application assembly.'
    Assert-Equal (Read-UninstallIdentity) $uninstallIdentity "Reinstall created a different uninstall identity."
    Assert-Equal ([IO.File]::ReadAllText($preferences)) '{"keep":"user data"}' "Reinstall modified a custom user file."

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
    }
    Assert-ProtectedState
    $verificationSucceeded = $true
    $upgradeResult = if ($baselineInstaller) { "verified actual $initialVersion -> $upgradedVersion upgrade" } else { 'cross-version upgrade not requested or verified' }
    Write-Output "Installer verification passed: current-user install, $upgradeResult, same-version reinstall, installed file activation and clean uninstall."
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
            try {
                $key.DeleteValue($foreignCandidate, $false)
                if ($portableCandidateKeys.Contains($path)) { $key.DeleteValue($portableProgId, $false) }
            }
            finally { $key.Dispose() }
        }
    }
    if ($cleanupSucceeded -and [IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory, $true) }
    if ([IO.Directory]::Exists($logs)) {
        [pscustomobject]@{
            SchemaVersion = 1
            Success = $verificationSucceeded
            CrossVersionUpgradeRequested = [bool]$baselineInstaller
            CrossVersionUpgradeVerified = $verificationSucceeded -and [bool]$baselineInstaller
            ExpectedBaselineVersion = if ($baselineInstaller) { $BaselineVersion } else { $null }
            PortableCandidatesSeeded = $portableCandidateKeys.Count
            BaselineInstallerSha256 = if ($baselineInstaller) { (Get-FileHash -LiteralPath $baselineInstaller -Algorithm SHA256).Hash } else { $null }
            TargetInstallerSha256 = (Get-FileHash -LiteralPath $installer -Algorithm SHA256).Hash
            Lifecycle = $lifecycle
            UninstallVerified = $uninstallCompleted -and $verificationSucceeded
            CleanupSucceeded = $cleanupSucceeded
        } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $logs 'lifecycle.json') -Encoding utf8
    }
    Write-Output "Installer smoke-test logs: $logs"
}
