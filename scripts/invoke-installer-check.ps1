#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$BaselineInstallerPath,
    [string]$FixtureDirectory,
    [string]$LogDirectory,
    [ValidateSet('0.4.0', '0.5.0', '0.6.0')][string]$BaselineVersion = '0.5.0',
    [ValidateRange(30, 600)][int]$DeadlineSeconds = 240
)
$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Installer verification requires Windows.' }
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') {
    throw 'Installer verification requires a disposable GitHub-hosted Windows runner.'
}
# Native setup or Shell COM calls can block the smoke's PowerShell thread.
# Keep the CI shell outside those calls so its deadline and log forwarding work.
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File',
    (Join-Path $PSScriptRoot 'check-installer.ps1'), '-InstallerPath', $InstallerPath,
    '-BaselineVersion', $BaselineVersion)) { $info.ArgumentList.Add($argument) }
foreach ($name in @('BaselineInstallerPath', 'FixtureDirectory', 'LogDirectory')) {
    $value = Get-Variable -Name $name -ValueOnly
    if ($value) { $info.ArgumentList.Add("-$name"); $info.ArgumentList.Add($value) }
}
$process = [Diagnostics.Process]::Start($info)
$watch = [Diagnostics.Stopwatch]::StartNew()
try {
    $pipes = @(
        @{ Reader = $process.StandardOutput; Pending = $process.StandardOutput.ReadLineAsync() },
        @{ Reader = $process.StandardError; Pending = $process.StandardError.ReadLineAsync() }
    )
    while ($true) {
        foreach ($pipe in $pipes) {
            while ($null -ne $pipe.Pending -and $pipe.Pending.IsCompleted) {
                $line = $pipe.Pending.GetAwaiter().GetResult()
                if ($null -eq $line) { $pipe.Pending = $null; break }
                Write-Host $line
                $pipe.Pending = $pipe.Reader.ReadLineAsync()
            }
        }
        if ($process.HasExited -and @($pipes | Where-Object { $null -ne $_.Pending }).Count -eq 0) { break }
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'InstallerSmokeDeadlineExceeded' }
        if (-not $process.HasExited) { [void]$process.WaitForExit(50) }
        else { Start-Sleep -Milliseconds 10 }
    }
    if ($process.ExitCode -ne 0) { throw "InstallerSmokeFailed:$($process.ExitCode)" }
}
finally {
    if (-not $process.HasExited) {
        $process.Kill($true)
        [void]$process.WaitForExit(5000)
    }
    $process.Dispose()
}
