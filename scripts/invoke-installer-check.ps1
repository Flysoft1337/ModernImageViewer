#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$InstallerPath,
    [string]$BaselineInstallerPath,
    [string]$FixtureDirectory,
    [string]$LogDirectory = (Join-Path $PSScriptRoot '../artifacts/installer-smoke'),
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
$logs = [IO.Path]::GetFullPath($LogDirectory)
[IO.Directory]::CreateDirectory($logs) | Out-Null
$outputPath = Join-Path $logs 'worker-output.log'
function Write-SmokeOutput([string]$Message) {
    $line = "[$([DateTimeOffset]::UtcNow.ToString('O'))] $Message"
    Add-Content -LiteralPath $outputPath -Value $line -Encoding utf8
    Write-Host $line
}
Write-SmokeOutput "Starting isolated installer worker; deadline $DeadlineSeconds seconds."
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
$nextHeartbeat = 15
try {
    $pipes = @(
        @{ Reader = $process.StandardOutput; Pending = $process.StandardOutput.ReadLineAsync() },
        @{ Reader = $process.StandardError; Pending = $process.StandardError.ReadLineAsync() }
    )
    while ($true) {
        foreach ($pipe in $pipes) {
            while ($null -ne $pipe.Pending -and $pipe.Pending.IsCompleted) {
                if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'InstallerSmokeDeadlineExceeded' }
                $line = $pipe.Pending.GetAwaiter().GetResult()
                if ($null -eq $line) { $pipe.Pending = $null; break }
                Write-SmokeOutput $line
                $pipe.Pending = $pipe.Reader.ReadLineAsync()
            }
        }
        if ($process.HasExited -and @($pipes | Where-Object { $null -ne $_.Pending }).Count -eq 0) { break }
        if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'InstallerSmokeDeadlineExceeded' }
        if ($watch.Elapsed.TotalSeconds -ge $nextHeartbeat) {
            Write-SmokeOutput "Installer worker elapsed $([int]$watch.Elapsed.TotalSeconds)s; exited: $($process.HasExited)."
            $nextHeartbeat += 15
        }
        if (-not $process.HasExited) { [void]$process.WaitForExit(50) }
        else { Start-Sleep -Milliseconds 10 }
    }
    if ($process.ExitCode -ne 0) { throw "InstallerSmokeFailed:$($process.ExitCode)" }
}
finally {
    try {
        if (-not $process.HasExited) {
            Write-SmokeOutput 'Stopping installer worker and descendants.'
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
    }
    finally {
        [pscustomobject]@{
            ElapsedSeconds = $watch.Elapsed.TotalSeconds
            DeadlineSeconds = $DeadlineSeconds
            Exited = $process.HasExited
            ExitCode = if ($process.HasExited) { $process.ExitCode } else { $null }
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $logs 'worker-status.json') -Encoding utf8
        $process.Dispose()
    }
}
