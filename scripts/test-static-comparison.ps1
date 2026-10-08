#Requires -Version 7.0
# Pure script regressions; do not launch a viewer or execute either measurement driver.
$ErrorActionPreference = 'Stop'
$tokens = $null
$parseErrors = $null
$source = [Management.Automation.Language.Parser]::ParseFile(
    (Join-Path $PSScriptRoot 'measure-static-comparison.ps1'), [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw 'ComparisonScriptParseFailed' }
foreach ($name in @('New-ForegroundObservation', 'Update-ForegroundObservation', 'Complete-ForegroundLoss',
    'Get-RunMetrics', 'Get-Timing', 'Get-Mean', 'Protect-Report')) {
    $function = $source.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $false)
    if ($null -eq $function) { throw 'MissingComparisonFunction' }
    . ([scriptblock]::Create($function.Extent.Text))
}

$assertions = 0
function Assert-Condition([bool]$Condition, [string]$Case) {
    if (-not $Condition) { throw "ComparisonRegressionFailed:$Case" }
    $script:assertions++
}

$state = New-ForegroundObservation
Update-ForegroundObservation $state $true $false $false 0
Update-ForegroundObservation $state $true $false $false 800
Update-ForegroundObservation $state $true $true $false 850
Assert-Condition ($state.Observed -and -not $state.Lost -and $state.PreActivationSamples -eq 2 -and $state.FirstForegroundMs -eq 850) 'InitialActivation'

Update-ForegroundObservation $state $true $false $true 1000
Update-ForegroundObservation $state $false $false $true 2000
Assert-Condition (-not $state.Lost -and $state.MeasurementCompletedMs -eq 1000 -and $state.CloseTailSamplesIgnored -eq 2) 'NormalCloseAfterMarker'

$state = New-ForegroundObservation
Update-ForegroundObservation $state $true $true $false 0
Update-ForegroundObservation $state $true $false $false 100
Assert-Condition $state.Lost 'SingleLossSampleFailsImmediately'
Update-ForegroundObservation $state $true $true $false 101
Assert-Condition ($state.Lost -and $state.LossIntervals[0].StartMs -eq 100 -and $state.LossIntervals[0].DurationMs -eq 1 -and $state.LossIntervals[0].Ending -eq 'ForegroundRecovered') 'RecoveryDoesNotEraseFailure'
Update-ForegroundObservation $state $false $false $true 200
Assert-Condition $state.Lost 'MarkerDoesNotEraseEarlierFailure'

$state = New-ForegroundObservation
Update-ForegroundObservation $state $true $true $false 0
Update-ForegroundObservation $state $true $false $false 100
Update-ForegroundObservation $state $true $false $false 125
Update-ForegroundObservation $state $true $false $false 600
Update-ForegroundObservation $state $false $false $true 625
Assert-Condition ($state.Lost -and $state.LossIntervals[0].Samples -eq 3 -and $state.LossIntervals[0].LastSampleMs -eq 600 -and $state.LossIntervals[0].DurationMs -eq 525) 'ContinuousLossRecorded'

$state = New-ForegroundObservation
Update-ForegroundObservation $state $true $true $false 0
Update-ForegroundObservation $state $false $false $false 25
Assert-Condition $state.Lost 'WindowDisappearsBeforeMarker'

$state = New-ForegroundObservation
Update-ForegroundObservation $state $true $false $false 0
Update-ForegroundObservation $state $false $false $true 1000
Assert-Condition (-not $state.Observed) 'MarkerDoesNotReplaceForegroundEvidence'

$state = New-ForegroundObservation
Update-ForegroundObservation $state $true $true $false 0
for ($interval = 1; $interval -le 40; $interval++) {
    Update-ForegroundObservation $state $true $false $false ($interval * 100)
    Update-ForegroundObservation $state $true $true $false ($interval * 100 + 25)
}
Assert-Condition ($state.Lost -and $state.LossIntervals.Count -eq 32 -and $state.DroppedLossIntervals -eq 8) 'DiagnosticsBounded'

# Test the optional marker statements alone; the normal drivers themselves are never invoked.
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('Miv-comparison-unit-' + [Guid]::NewGuid().ToString('N'))
[void][IO.Directory]::CreateDirectory($temporary)
try {
    foreach ($driver in @('startup', 'browsing')) {
        $driverAst = [Management.Automation.Language.Parser]::ParseFile(
            (Join-Path $PSScriptRoot "measure-$driver.ps1"), [ref]$tokens, [ref]$parseErrors)
        Assert-Condition ($parseErrors.Count -eq 0) "$driver-Parse"
        $marker = $driverAst.Find({ param($node)
            $node -is [Management.Automation.Language.IfStatementAst] -and
            $node.Extent.Text -match '^if \(\$ObservationCompleteSignalPath' -and
            $node.Extent.Text -match 'WriteAllText'
        }, $true)
        Assert-Condition ($null -ne $marker) "$driver-MarkerGuardExists"
        $measurementTry = $marker.Parent
        while ($measurementTry -and $measurementTry -isnot [Management.Automation.Language.TryStatementAst]) { $measurementTry = $measurementTry.Parent }
        Assert-Condition ($measurementTry -and $marker.Extent.EndOffset -lt $measurementTry.Finally.Extent.StartOffset -and
            $measurementTry.Finally.Extent.Text -match 'CloseMainWindow') "$driver-MarkerBeforeClose"
        $ObservationCompleteSignalPath = $null
        $iteration = 1
        $Iterations = 1
        . ([scriptblock]::Create($marker.Extent.Text))
        Assert-Condition (@(Get-ChildItem -LiteralPath $temporary -Force).Count -eq 0) "$driver-DefaultNoMarkerIO"
        $ObservationCompleteSignalPath = Join-Path $temporary 'completion.marker'
        if ($driver -eq 'startup') {
            $Iterations = 2
            . ([scriptblock]::Create($marker.Extent.Text))
            Assert-Condition (-not [IO.File]::Exists($ObservationCompleteSignalPath)) 'StartupFinalIterationOnly'
            $iteration = 2
        }
        . ([scriptblock]::Create($marker.Extent.Text))
        Assert-Condition ([IO.File]::ReadAllText($ObservationCompleteSignalPath) -eq 'ObservationComplete') "$driver-StableMarkerContent"
        [IO.File]::Delete($ObservationCompleteSignalPath)
    }
}
finally { [IO.Directory]::Delete($temporary, $true) }

# Minimum request identity must win even if entries are out of order.
$phases = [Collections.Generic.List[object]]::new()
$phases.Add(@{ RequestId = 0; Phase = 'WindowVisible'; SinceStartMs = 0; SinceRequestMs = $null })
foreach ($id in @(9, 2, 3, 4, 5, 6, 7, 8, 10)) {
    $phases.Add(@{ RequestId = $id; Phase = 'OpenRequested'; SinceStartMs = $id * 100; SinceRequestMs = 0 })
    foreach ($phase in @('FirstRecognizablePainted', 'NavigationAvailable', 'RequiredDetailPainted')) {
        $phases.Add(@{ RequestId = $id; Phase = $phase; SinceStartMs = $id * 100 + 10; SinceRequestMs = 10 })
    }
    if ($id -ne 2) { $phases.Add(@{ RequestId = $id; Phase = 'NeighborSwitchCompleted'; SinceStartMs = $id * 100 + 10; SinceRequestMs = 10 }) }
}
foreach ($id in @(5, 8)) {
    $phases.Add(@{ RequestId = $id; Phase = 'RequiredDetailRequested'; SinceStartMs = $id * 100 + 20; SinceRequestMs = 20 })
    $phases.Add(@{ RequestId = $id; Phase = 'RequiredDetailPainted'; SinceStartMs = $id * 100 + 50; SinceRequestMs = 50 })
}
$startup = @{ OpenedImage = $false; Samples = @(@{ WindowInputIdleMs = 200 }) }
$browsing = @{
    InputCount = 8; NeighborSwitches = 8; RapidBurst = 4; RefreshEvery = 4; DetailEvery = 3
    PhaseObservation = @{ Success = $true; DroppedRequests = 0; CompletedNeighborSwitches = 8
        AllRequiredDetailsPainted = $true; DetailBudgetLimitedCount = 0; Phases = $phases.ToArray() }
}
$metrics = Get-RunMetrics $startup $browsing
Assert-Condition ($metrics.FirstRequestId -eq 2 -and $metrics.RequestedDetailMeanMs -eq 30 -and $metrics.RequestedDetailSamples -eq 2) 'DetailProtocolAndMinimumRequest'
$oldProtocolRejected = $false
$browsing.DetailEvery = 4
try { $null = Get-RunMetrics $startup $browsing } catch { $oldProtocolRejected = $_.Exception.Message -eq 'InvalidReport' }
Assert-Condition $oldProtocolRejected 'RejectOldDetailProtocol'
$detailEdges = @(for ($switch = 1; $switch -le 8; $switch++) {
    if ($switch % 3 -eq 0) { if ($switch % 2) { 2048 } else { 64 } }
})
Assert-Condition ($detailEdges.Count -eq 2 -and $detailEdges[0] -eq 2048 -and $detailEdges[1] -eq 64) 'LargeAndSmallDetailDemand'

$protected = Protect-Report @{ PhaseObservation = @{ Failure = 'Open failed at C:\Users\private\photo.png'; Extra = @('C:\Users\private\image', '\\server\share\image', '/home/private/image') }; InputCount = 8 }
$protectedJson = ConvertTo-Json -InputObject $protected -Depth 8
Assert-Condition ($protectedJson -notmatch 'private|server|Open failed' -and $protected.InputCount -eq 8 -and $protected.PhaseObservation.Failure -eq 'RedactedFailure') 'InheritedReportPrivacy'

Write-Output "Comparison regressions passed: $assertions assertions; no viewer or performance run."
