#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineAppPath,
    [Parameter(Mandatory)][string]$CandidateAppPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 100)][int]$Pairs = 3,
    [ValidateRange(0, 20)][int]$WarmupPairs = 1,
    [ValidateRange(60, 3600)][int]$DeadlineSeconds = 600
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'static-comparison-policy.ps1')
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
# Never read evidence from a previous invocation if the new driver rejects reuse.
if ([IO.File]::Exists($OutputDirectory) -or ([IO.Directory]::Exists($OutputDirectory) -and
    @(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count)) { throw 'OutputDirectoryNotEmpty' }
$info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
$info.UseShellExecute = $false
$info.CreateNoWindow = $true
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
foreach ($argument in @('-NoProfile', '-NonInteractive', '-File', (Join-Path $PSScriptRoot 'measure-static-comparison.ps1'),
    '-BaselineAppPath', $BaselineAppPath, '-CandidateAppPath', $CandidateAppPath,
    '-OutputDirectory', $OutputDirectory, '-Pairs', "$Pairs", '-WarmupPairs', "$WarmupPairs")) {
    $info.ArgumentList.Add($argument)
}
$process = [Diagnostics.Process]::Start($info)
try {
    # Drain both pipes concurrently so verbose output cannot block a child process.
    $stdout = $process.StandardOutput.ReadToEndAsync()
    $stderr = $process.StandardError.ReadToEndAsync()
    if (-not $process.WaitForExit($DeadlineSeconds * 1000)) {
        $process.Kill($true)
        [void]$process.WaitForExit(5000)
        throw 'ComparisonDeadlineExceeded'
    }
    # Descendants can inherit pipe handles after the driver exits. Bound draining
    # as well as process execution; never wait indefinitely on an inherited pipe.
    if (-not [Threading.Tasks.Task]::WaitAll([Threading.Tasks.Task[]]@($stdout, $stderr), 5000)) {
        throw 'ComparisonOutputDrainTimeout'
    }
    $reportPath = Join-Path $OutputDirectory 'index.json'
    $report = Get-Content -LiteralPath $reportPath -Raw | ConvertFrom-Json -AsHashtable
    try { $disposition = Get-StaticComparisonDisposition $report $process.ExitCode }
    catch {
        [pscustomobject]@{ SchemaVersion = 1; Disposition = 'Failed'; Failure = $_.Exception.Message
            ChildExitCode = $process.ExitCode; PerformanceAccepted = $false
            CompletePairs = $report.Statistics.CompletePairs } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'disposition.json') -Encoding utf8
        throw
    }
    [pscustomobject]@{ SchemaVersion = 1; Disposition = $disposition; Failure = $report.Failure
        ChildExitCode = $process.ExitCode; PerformanceAccepted = $disposition -eq 'Valid'
        CompletePairs = $report.Statistics.CompletePairs } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'disposition.json') -Encoding utf8
    if ($disposition -eq 'Inconclusive') { Write-Warning "Static comparison Inconclusive: $($report.Failure). No performance acceptance." }
    else { Write-Output 'Valid static comparison within the existing 10% review threshold.' }
}
finally { $process.Dispose() }
