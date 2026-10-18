#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineAppPath,
    [Parameter(Mandatory)][string]$CandidateAppPath,
    [Parameter(Mandatory)][string]$OutputDirectory,
    [ValidateRange(1, 100)][int]$Pairs = 3,
    [ValidateRange(0, 20)][int]$WarmupPairs = 1,
    [ValidateRange(60, 3600)][int]$DeadlineSeconds = 600,
    [switch]$RequireValid,
    [string]$PerformanceAcceptancePath
)
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'static-comparison-policy.ps1')
$review = $null
$reviewedLimits = @{}
if ($PerformanceAcceptancePath) {
    $review = Get-Content -LiteralPath $PerformanceAcceptancePath -Raw | ConvertFrom-Json -AsHashtable
    $repository = Split-Path $PSScriptRoot -Parent
    [xml]$properties = Get-Content -LiteralPath (Join-Path $repository 'Directory.Build.props') -Raw
    if ($review.SchemaVersion -ne 1 -or $review.Version -cne [string]$properties.Project.PropertyGroup.Version -or
        $review.Comparison -cne 'SameSourceFlatVsSingleFile' -or
        $review.ReviewedCommit -cnotmatch '^[0-9a-f]{40}$' -or -not $review.UserDecision -or
        $review.MaximumPairRatios -isnot [hashtable]) { throw 'InvalidPerformanceReview' }
    # Approval applies only to the reviewed product and package, including build inputs.
    & git -C $repository diff --quiet $review.ReviewedCommit -- src packaging third_party `
        Directory.Build.props Directory.Packages.props global.json ModernImageViewer.slnx `
        scripts/build-installer.ps1 scripts/build-raw-native.ps1
    if ($LASTEXITCODE -ne 0) { throw 'ReviewedProductChanged' }
    $reviewedLimits = $review.MaximumPairRatios
}
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
    if ($review) {
        $baselineVersion = [string]$report.BinaryIdentities.A.ProductVersion
        $candidateVersion = [string]$report.BinaryIdentities.B.ProductVersion
        if ($baselineVersion -cne $candidateVersion -or
            $candidateVersion.Split('+')[0] -cne $review.Version) { throw 'InvalidReviewedComparisonIdentity' }
    }
    try { $disposition = Get-StaticComparisonDisposition $report $process.ExitCode -RequireValid:$RequireValid -ReviewedLimits $reviewedLimits }
    catch {
        [pscustomobject]@{ SchemaVersion = 1; Disposition = 'Failed'; Failure = $_.Exception.Message
            ChildExitCode = $process.ExitCode; PerformanceAccepted = $false
            CompletePairs = $report.Statistics.CompletePairs } |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'disposition.json') -Encoding utf8
        throw
    }
    [pscustomobject]@{ SchemaVersion = 1; Disposition = $disposition; Failure = $report.Failure
        ChildExitCode = $process.ExitCode; PerformanceAccepted = $disposition -eq 'Valid'
        RiskAccepted = $disposition -eq 'AcceptedRisk'; Review = $review
        CompletePairs = $report.Statistics.CompletePairs } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputDirectory 'disposition.json') -Encoding utf8
    if ($disposition -eq 'Inconclusive') { Write-Warning "Static comparison Inconclusive: $($report.Failure). No performance acceptance." }
    elseif ($disposition -eq 'AcceptedRisk') { Write-Warning 'Packaging performance exceeds 10%; explicit, bounded release approval applied. See disposition.json.' }
    else { Write-Output 'Valid static comparison within the existing 10% review threshold.' }
}
finally { $process.Dispose() }
