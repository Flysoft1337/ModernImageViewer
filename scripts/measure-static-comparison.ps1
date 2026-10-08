#Requires -Version 7.0
<#
.SYNOPSIS
Runs alternating, same-machine static observations using the existing normal drivers.
.EXAMPLE
./scripts/measure-static-comparison.ps1 -BaselineAppPath ./artifacts/release-closeout/builds/0.5/ModernImageViewer.App.exe -CandidateAppPath ./artifacts/release-closeout/builds/0.6/ModernImageViewer.App.exe -Pairs 8 -WarmupPairs 1
.NOTES
Keep the desktop unlocked and do not interact with other windows during the run.
Warmups are saved but excluded from statistics. Ratios are candidate / baseline.
Reports retain driver fields, with path-bearing strings and exception text redacted.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$BaselineAppPath,
    [Parameter(Mandatory)][string]$CandidateAppPath,
    [ValidateRange(1, 100)][int]$Pairs = 6,
    [ValidateRange(0, 20)][int]$WarmupPairs = 1,
    [string]$OutputDirectory = ('artifacts/static-comparison/' + [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)),
    [ValidateRange(1, 30)][int]$ObservationSeconds = 1,
    [ValidateRange(1, 120)][int]$IdleSeconds = 2,
    [ValidateRange(10, 3600)][int]$TimeoutSeconds = 180,
    [ValidateSet('Auto', 'FrameworkDependent', 'SelfContained')][string]$BuildKind = 'Auto'
)

$ErrorActionPreference = 'Stop'

function Get-StableFailure($ErrorRecord) {
    $known = @('WindowsRequired', 'ViewerAlreadyRunning', 'IdenticalAssemblies', 'MissingManagedAssembly',
        'OutputDirectoryNotEmpty', 'ComparisonAlreadyRunning', 'ChildScriptFailed', 'ChildScriptTimeout',
        'MissingReport', 'InvalidReport', 'IncompleteSwitches', 'IncompleteDetails', 'InvalidTiming',
        'ForegroundNotObserved', 'ForegroundWindowLost', 'MissingCompletionSignal', 'MultipleViewers', 'BinaryChanged', 'ViewportChanged')
    if ($ErrorRecord.Exception.Message -in $known) { return $ErrorRecord.Exception.Message }
    return $ErrorRecord.Exception.GetType().Name
}

function Protect-Report($Value, [string]$Field = '') {
    if ($null -eq $Value) { return $null }
    if ($Value -is [string]) {
        if ($Field -match '(?i)failure|error|exception|stacktrace|message') {
            if ($Value -match '^[A-Za-z][A-Za-z0-9]{0,80}Exception$' -or
                $Value -in @('NavigationUnavailable', 'ViewerAlreadyRunning', 'ChildScriptFailed', 'ChildScriptTimeout',
                    'MissingReport', 'InvalidReport', 'IncompleteSwitches', 'IncompleteDetails', 'InvalidTiming',
                    'ForegroundNotObserved', 'ForegroundWindowLost', 'MissingCompletionSignal', 'MultipleViewers', 'BinaryChanged', 'ViewportChanged',
                    'WindowsRequired', 'IdenticalAssemblies', 'MissingManagedAssembly', 'OutputDirectoryNotEmpty', 'ComparisonAlreadyRunning')) {
                return $Value
            }
            return 'RedactedFailure'
        }
        # Test parsed strings, including UNC, drive-rooted, URI and POSIX paths.
        if ($Value -match '(?i)[a-z]:[\\/]|\\\\|(?:file|https?)://|(?:^|[\s"''(=])/[\S]+') { return '[redacted-path]' }
        return $Value
    }
    if ($Value -is [System.Collections.IDictionary] -or $Value -is [pscustomobject]) {
        $result = [ordered]@{}
        $keys = if ($Value -is [System.Collections.IDictionary]) { @($Value.Keys) } else { @($Value.PSObject.Properties.Name) }
        foreach ($key in $keys) {
            $safeKey = Protect-Report ([string]$key)
            if ($safeKey -eq '[redacted-path]') { continue }
            $item = if ($Value -is [System.Collections.IDictionary]) { $Value[$key] } else { $Value.$key }
            $result[$safeKey] = Protect-Report $item $key
        }
        return $result
    }
    if ($Value -is [System.Collections.IEnumerable]) {
        $items = @(foreach ($item in $Value) { Protect-Report $item $Field })
        return ,$items
    }
    return $Value
}

function Write-SafeJson($Value, [string]$Path) {
    $json = ConvertTo-Json -InputObject (Protect-Report $Value) -Depth 30
    [IO.File]::WriteAllText($Path, $json, [Text.UTF8Encoding]::new($false))
}

function Get-BinaryIdentity([string]$Executable) {
    $directory = [IO.Path]::GetDirectoryName($Executable)
    if (-not [IO.File]::Exists([IO.Path]::ChangeExtension($Executable, 'dll'))) { throw 'MissingManagedAssembly' }
    $assemblies = @(Get-ChildItem -LiteralPath $directory -Filter 'ModernImageViewer*.dll' -File | Sort-Object Name | ForEach-Object {
        $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName)
        [pscustomobject]@{
            Name = $_.Name
            Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
            ProductVersion = $version.ProductVersion
        }
    })
    $fingerprint = [Text.Encoding]::UTF8.GetBytes(($assemblies | ForEach-Object { $_.Name + ':' + $_.Sha256 }) -join "`n")
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($Executable)
    [pscustomobject]@{
        ProductVersion = $version.ProductVersion
        FileVersion = $version.FileVersion
        ExecutableSha256 = (Get-FileHash -LiteralPath $Executable -Algorithm SHA256).Hash
        AssemblySetSha256 = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($fingerprint))
        Assemblies = $assemblies
    }
}

function Assert-NoViewer([string[]]$Names) {
    if (@(Get-Process -Name $Names -ErrorAction SilentlyContinue).Count -ne 0) { throw 'ViewerAlreadyRunning' }
}

function New-ComparisonInputs([string]$Directory) {
    [void][IO.Directory]::CreateDirectory($Directory)
    Add-Type -AssemblyName System.Drawing.Common
    # The same eight images and dimensions as measure-browsing.ps1, shared by both versions.
    for ($index = 0; $index -lt 8; $index++) {
        $edge = if ($index % 2 -eq 0) { 64 } else { 2048 }
        $bitmap = [Drawing.Bitmap]::new($edge, $edge)
        $graphics = $null
        try {
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            $graphics.Clear([Drawing.Color]::FromArgb(255, ($index * 17) % 256, ($index * 29) % 256, ($index * 41) % 256))
            $bitmap.Save((Join-Path $Directory ('sample-{0:D4}.png' -f $index)), [Drawing.Imaging.ImageFormat]::Png)
        }
        finally { if ($graphics) { $graphics.Dispose() }; $bitmap.Dispose() }
    }
}

function New-ForegroundObservation {
    [pscustomobject]@{
        Observed = $false
        VisibleSamples = 0
        PreActivationSamples = 0
        FirstVisibleMs = $null
        LastPreActivationMs = $null
        FirstForegroundMs = $null
        Lost = $false
        LossSamples = 0
        PendingLoss = $null
        LossIntervals = [Collections.Generic.List[object]]::new()
        DroppedLossIntervals = 0
        MeasurementCompletedMs = $null
        CloseTailSamplesIgnored = 0
    }
}

function Complete-ForegroundLoss($State, [double]$ElapsedMs, [string]$Ending) {
    if ($null -ne $State.PendingLoss) {
        $State.PendingLoss.EndMs = $ElapsedMs
        $State.PendingLoss.DurationMs = $ElapsedMs - $State.PendingLoss.StartMs
        $State.PendingLoss.Ending = $Ending
        $State.PendingLoss = $null
    }
}

function Update-ForegroundObservation($State, [bool]$Visible, [bool]$Foreground, [bool]$MeasurementComplete, [double]$ElapsedMs) {
    # Only the driver's explicit post-measurement marker ends checks; a raw report cannot do so.
    if ($MeasurementComplete -and $null -eq $State.MeasurementCompletedMs) {
        $State.MeasurementCompletedMs = $ElapsedMs
        Complete-ForegroundLoss $State $ElapsedMs 'MeasurementComplete'
    }
    if ($null -ne $State.MeasurementCompletedMs) { $State.CloseTailSamplesIgnored++; return }
    if ($Visible) {
        $State.VisibleSamples++
        if ($null -eq $State.FirstVisibleMs) { $State.FirstVisibleMs = $ElapsedMs }
    }
    if (-not $State.Observed) {
        if ($Visible -and $Foreground) { $State.Observed = $true; $State.FirstForegroundMs = $ElapsedMs }
        elseif ($Visible) { $State.PreActivationSamples++; $State.LastPreActivationMs = $ElapsedMs }
        return
    }
    if (-not $Visible -or -not $Foreground) {
        $State.Lost = $true
        $State.LossSamples++
        if ($null -eq $State.PendingLoss) {
            $State.PendingLoss = [pscustomobject]@{
                StartMs = $ElapsedMs; LastSampleMs = $ElapsedMs; EndMs = $null; DurationMs = 0.0
                Samples = 0; Ending = 'Pending'
            }
            if ($State.LossIntervals.Count -lt 32) { $State.LossIntervals.Add($State.PendingLoss) }
            else { $State.DroppedLossIntervals++ }
        }
        $State.PendingLoss.Samples++
        $State.PendingLoss.LastSampleMs = $ElapsedMs
        $State.PendingLoss.DurationMs = $ElapsedMs - $State.PendingLoss.StartMs
    }
    else { Complete-ForegroundLoss $State $ElapsedMs 'ForegroundRecovered' }
}

function Invoke-NormalObservation([string]$Driver, [string[]]$Arguments, [string]$ReportPath,
    [string]$Executable, [string[]]$ViewerNames, [int]$DeadlineSeconds, [string]$CompletionSignalPath) {
    Assert-NoViewer $ViewerNames
    $info = [Diagnostics.ProcessStartInfo]::new((Join-Path $PSHOME 'pwsh.exe'))
    $info.UseShellExecute = $false
    $info.CreateNoWindow = $true
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    foreach ($argument in @('-NoLogo', '-NoProfile', '-NonInteractive', '-File', $Driver) + $Arguments) { $info.ArgumentList.Add($argument) }
    # Prevent an inherited opt-in animation/browsing driver from contaminating startup.
    foreach ($name in @($info.Environment.Keys)) {
        if ($name -match '^MIV_(BROWSING|ANIMATION|STARTUP)_') { [void]$info.Environment.Remove($name) }
    }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $failure = $null
    $foregroundState = New-ForegroundObservation
    $started = $false
    try {
        $started = $process.Start()
        if (-not $started) { throw 'ChildScriptFailed' }
        # Drain output asynchronously, but never persist exception messages or console output.
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        $viewerName = [IO.Path]::GetFileNameWithoutExtension($Executable)
        while (-not $process.WaitForExit(25)) {
            if ($watch.Elapsed.TotalSeconds -ge $DeadlineSeconds) { throw 'ChildScriptTimeout' }
            if ([IO.File]::Exists($CompletionSignalPath)) {
                Update-ForegroundObservation $foregroundState $false $false $true $watch.Elapsed.TotalMilliseconds
                continue
            }
            $visible = $false
            $isForeground = $false
            $viewers = @(Get-Process -Name $ViewerNames -ErrorAction SilentlyContinue)
            try {
                if ($viewers.Count -gt 1) { $failure = 'MultipleViewers' }
                foreach ($viewer in $viewers) {
                    $viewer.Refresh()
                    if ($viewer.HasExited -or $viewer.ProcessName -ne $viewerName -or
                        $viewer.MainWindowHandle -eq [IntPtr]::Zero -or
                        $viewer.MainWindowTitle -notmatch 'Modern Image Viewer|现代图片查看器') { continue }
                    if (-not [MivStaticComparison.WindowState]::IsWindowVisible($viewer.MainWindowHandle)) { continue }
                    $visible = $true
                    [uint32]$foregroundPid = 0
                    [void][MivStaticComparison.WindowState]::GetWindowThreadProcessId(
                        [MivStaticComparison.WindowState]::GetForegroundWindow(), [ref]$foregroundPid)
                    $isForeground = $foregroundPid -eq $viewer.Id -and -not [MivStaticComparison.WindowState]::IsIconic($viewer.MainWindowHandle)
                }
            }
            finally { foreach ($viewer in $viewers) { $viewer.Dispose() } }
            # Recheck after sampling: the marker is written before close can affect this window.
            Update-ForegroundObservation $foregroundState $visible $isForeground ([IO.File]::Exists($CompletionSignalPath)) $watch.Elapsed.TotalMilliseconds
            if ($foregroundState.Lost -and -not $failure) { $failure = 'ForegroundWindowLost' }
        }
        [void]$stdout.GetAwaiter().GetResult()
        [void]$stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { $failure = 'ChildScriptFailed' }
        if ([IO.File]::Exists($CompletionSignalPath)) {
            Update-ForegroundObservation $foregroundState $false $false $true $watch.Elapsed.TotalMilliseconds
        }
        elseif (-not $failure) { $failure = 'MissingCompletionSignal' }
        if (-not $foregroundState.Observed -and -not $failure) { $failure = 'ForegroundNotObserved' }
    }
    catch { $failure = Get-StableFailure $_ }
    finally {
        if ($started -and -not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        $process.Dispose()
    }
    $report = $null
    if ([IO.File]::Exists($ReportPath)) {
        try { $report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json -AsHashtable }
        catch { $failure = 'InvalidReport' }
    }
    elseif (-not $failure) { $failure = 'MissingReport' }
    Complete-ForegroundLoss $foregroundState $watch.Elapsed.TotalMilliseconds 'ScriptExited'
    [pscustomobject]@{
        Success = -not $failure
        Failure = $failure
        ForegroundObserved = $foregroundState.Observed
        ForegroundSamples = $foregroundState.VisibleSamples
        ForegroundDetail = $foregroundState
        Report = $report
    }
}

function Get-Timing($Value) {
    if ($null -eq $Value -or $Value -is [string] -or $Value -is [bool]) { throw 'InvalidTiming' }
    $number = [double]$Value
    if (-not [double]::IsFinite($number) -or $number -lt 0) { throw 'InvalidTiming' }
    return $number
}

function Get-Mean($Values) {
    $numbers = @($Values)
    if (-not $numbers.Count) { throw 'InvalidTiming' }
    return [double](($numbers | Measure-Object -Average).Average)
}

function Get-RunMetrics($Startup, $Browsing) {
    if (@($Startup.Samples).Count -ne 1 -or $Startup.OpenedImage -ne $false -or $Browsing.InputCount -ne 8 -or
        $Browsing.NeighborSwitches -ne 8 -or $Browsing.RapidBurst -ne 4 -or $Browsing.RefreshEvery -ne 4 -or $Browsing.DetailEvery -ne 3) { throw 'InvalidReport' }
    $observation = $Browsing.PhaseObservation
    if ($observation.Success -ne $true -or $observation.DroppedRequests -ne 0) { throw 'InvalidReport' }
    if ($observation.CompletedNeighborSwitches -ne 8) { throw 'IncompleteSwitches' }
    if ($observation.AllRequiredDetailsPainted -ne $true -or $observation.DetailBudgetLimitedCount -ne 0) { throw 'IncompleteDetails' }
    $phases = @($observation.Phases)
    $opens = @($phases | Where-Object { $_.Phase -eq 'OpenRequested' -and $_.RequestId -gt 0 } | Sort-Object RequestId)
    if (-not $opens.Count -or 'WindowVisible' -notin $phases.Phase) { throw 'InvalidReport' }
    $firstRequest = $opens[0].RequestId
    $first = @($phases | Where-Object RequestId -eq $firstRequest)
    $firstTimings = @{}
    foreach ($phase in @('FirstRecognizablePainted', 'NavigationAvailable', 'RequiredDetailPainted')) {
        $matches = @($first | Where-Object Phase -eq $phase | Sort-Object SinceStartMs)
        if (-not $matches.Count) { throw 'InvalidReport' }
        $firstTimings[$phase] = Get-Timing $matches[0].SinceRequestMs
    }
    $switches = @($phases | Where-Object Phase -eq 'NeighborSwitchCompleted')
    if ($switches.Count -ne 8 -or @($switches.RequestId | Sort-Object -Unique).Count -ne 8) { throw 'IncompleteSwitches' }
    $switchTimings = @($switches | ForEach-Object { Get-Timing $_.SinceRequestMs })
    $detailRequests = @($phases | Where-Object Phase -eq 'RequiredDetailRequested')
    if ($detailRequests.Count -ne 2) { throw 'IncompleteDetails' }
    $detailTimings = @(foreach ($request in $detailRequests) {
        $completion = @($phases | Where-Object {
            $_.RequestId -eq $request.RequestId -and $_.Phase -eq 'RequiredDetailPainted' -and $_.SinceStartMs -ge $request.SinceStartMs
        } | Sort-Object SinceStartMs)
        if (-not $completion.Count) { throw 'IncompleteDetails' }
        Get-Timing ((Get-Timing $completion[0].SinceStartMs) - (Get-Timing $request.SinceStartMs))
    })
    [pscustomobject]@{
        StartupWindowInputIdleMs = Get-Timing $Startup.Samples[0].WindowInputIdleMs
        FirstRecognizableMs = $firstTimings.FirstRecognizablePainted
        NavigationAvailableMs = $firstTimings.NavigationAvailable
        NeighborSwitchMeanMs = Get-Mean $switchTimings
        FirstRequiredDetailMs = $firstTimings.RequiredDetailPainted
        RequestedDetailMeanMs = Get-Mean $detailTimings
        FirstRequestId = $firstRequest
        StartupSamples = 1
        NeighborSwitchSamples = $switchTimings.Count
        RequestedDetailSamples = $detailTimings.Count
    }
}

function Get-Median($Values) {
    $numbers = @($Values | Sort-Object)
    if (-not $numbers.Count) { return $null }
    $middle = [int][Math]::Floor($numbers.Count / 2)
    if ($numbers.Count % 2) { return [double]$numbers[$middle] }
    return ([double]$numbers[$middle - 1] + [double]$numbers[$middle]) / 2
}

function Get-ComparisonStatistics($Runs) {
    $metrics = @('StartupWindowInputIdleMs', 'FirstRecognizableMs', 'NavigationAvailableMs',
        'NeighborSwitchMeanMs', 'FirstRequiredDetailMs', 'RequestedDetailMeanMs')
    $measured = @($Runs | Where-Object { -not $_.Warmup })
    $completePairs = @(foreach ($group in ($measured | Group-Object Pair)) {
        if ($group.Count -eq 2 -and @($group.Group | Where-Object Success -eq $true).Count -eq 2) { $group }
    })
    $versions = [ordered]@{}
    foreach ($label in @('A', 'B')) {
        $valid = @($measured | Where-Object { $_.Version -eq $label -and $_.Success })
        $medians = [ordered]@{}
        foreach ($metric in $metrics) { $medians[$metric] = Get-Median @($valid | ForEach-Object { $_.Metrics.$metric }) }
        $versions[$label] = [pscustomobject]@{
            SuccessfulRuns = $valid.Count
            StartupSamples = $valid.Count
            FirstRequestSamples = $valid.Count
            NeighborSwitchSamples = ($valid | ForEach-Object { $_.Metrics.NeighborSwitchSamples } | Measure-Object -Sum).Sum
            RequestedDetailSamples = ($valid | ForEach-Object { $_.Metrics.RequestedDetailSamples } | Measure-Object -Sum).Sum
            MedianMs = $medians
        }
    }
    $ratios = @(foreach ($group in $completePairs) {
        $a = @($group.Group | Where-Object Version -eq 'A')[0]
        $b = @($group.Group | Where-Object Version -eq 'B')[0]
        $values = [ordered]@{}
        foreach ($metric in $metrics) {
            $values[$metric] = if ($a.Metrics.$metric -gt 0) { $b.Metrics.$metric / $a.Metrics.$metric } else { $null }
        }
        [pscustomobject]@{ Pair = $a.Pair; Order = $a.Order; CandidateOverBaseline = $values }
    })
    [pscustomobject]@{ Versions = $versions; CompletePairs = $completePairs.Count; PairRatios = $ratios }
}

$runs = [Collections.Generic.List[object]]::new()
$success = $false
$failure = $null
$destination = $null
$temporary = $null
$identities = [ordered]@{}
$mutex = $null
$ownsMutex = $false
$expectedViewport = $null
try {
    if (-not $IsWindows) { throw 'WindowsRequired' }
    $destination = [IO.Path]::GetFullPath($OutputDirectory)
    if ([IO.Directory]::Exists($destination) -and @(Get-ChildItem -LiteralPath $destination -Force).Count) {
        $destination = $null
        throw 'OutputDirectoryNotEmpty'
    }
    [void][IO.Directory]::CreateDirectory($destination)
    $mutex = [Threading.Mutex]::new($false, 'Local\ModernImageViewer-StaticComparison')
    try { $ownsMutex = $mutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'ComparisonAlreadyRunning' }
    $executables = @{
        A = (Resolve-Path -LiteralPath $BaselineAppPath).Path
        B = (Resolve-Path -LiteralPath $CandidateAppPath).Path
    }
    $viewerNames = @('ModernImageViewer.App', 'ModernImageViewer', [IO.Path]::GetFileNameWithoutExtension($executables.A),
        [IO.Path]::GetFileNameWithoutExtension($executables.B)) | Sort-Object -Unique
    Assert-NoViewer $viewerNames
    foreach ($label in @('A', 'B')) { $identities[$label] = Get-BinaryIdentity $executables[$label] }
    if ($identities.A.AssemblySetSha256 -eq $identities.B.AssemblySetSha256) { throw 'IdenticalAssemblies' }
    if (-not ('MivStaticComparison.WindowState' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace MivStaticComparison {
    public static class WindowState {
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] public static extern bool IsIconic(IntPtr window);
    }
}
'@
    }
    $temporary = Join-Path ([IO.Path]::GetTempPath()) ('ModernImageViewer-static-comparison-' + [Guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($temporary)
    $images = Join-Path $temporary 'images'
    New-ComparisonInputs $images
    for ($sequence = 1; $sequence -le $WarmupPairs + $Pairs; $sequence++) {
        $warmup = $sequence -le $WarmupPairs
        $pair = if ($warmup) { $sequence } else { $sequence - $WarmupPairs }
        $order = if ($sequence % 2) { @('A', 'B') } else { @('B', 'A') }
        foreach ($label in $order) {
            $prefix = '{0}-{1:D3}-{2}' -f $(if ($warmup) { 'warmup' } else { 'pair' }), $pair, $label
            $run = [pscustomobject]@{
                Pair = $pair; Warmup = $warmup; Order = $order -join ''; Version = $label
                Success = $false; Failure = $null; Metrics = $null
                Reports = @{ Startup = "$prefix-startup.json"; Browsing = "$prefix-browsing.json" }
                DriverStatus = [ordered]@{}
            }
            $runs.Add($run)
            try {
                foreach ($kind in @('startup', 'browsing')) {
                    $rawPath = Join-Path $temporary "$prefix-$kind.json"
                    $signalPath = Join-Path $temporary "$prefix-$kind.complete"
                    $arguments = @('-AppPath', $executables[$label], '-OutputPath', $rawPath, '-ObservationCompleteSignalPath', $signalPath)
                    if ($kind -eq 'startup') { $arguments += @('-Iterations', '1', '-ObservationSeconds', "$ObservationSeconds") }
                    else {
                        $arguments += @('-ImageDirectory', $images, '-GeneratedImageCount', '8', '-NeighborSwitches', '8', '-RapidBurst', '4',
                            '-RefreshEvery', '4', '-DetailEvery', '3', '-IdleSeconds', "$IdleSeconds", '-TimeoutSeconds', "$TimeoutSeconds", '-BuildKind', $BuildKind)
                    }
                    $result = Invoke-NormalObservation (Join-Path $PSScriptRoot "measure-$kind.ps1") $arguments $rawPath $executables[$label] $viewerNames $(if ($kind -eq 'startup') { 60 + $ObservationSeconds } else { $TimeoutSeconds + $IdleSeconds + 60 }) $signalPath
                    $run.DriverStatus[$kind] = [pscustomobject]@{
                        Success = $result.Success; Failure = $result.Failure
                        ForegroundObserved = $result.ForegroundObserved; ForegroundSamples = $result.ForegroundSamples
                        ForegroundDetail = $result.ForegroundDetail
                        RawReportAvailable = $null -ne $result.Report
                    }
                    $saved = if ($null -ne $result.Report) { $result.Report } else { @{ Success = $false; Failure = $result.Failure; RawReportAvailable = $false } }
                    Write-SafeJson $saved (Join-Path $destination $run.Reports.$kind)
                    if (-not $result.Success) { throw $result.Failure }
                    if ($kind -eq 'startup') { $startup = $result.Report } else { $browsing = $result.Report }
                }
                $run.Metrics = Get-RunMetrics $startup $browsing
                $viewport = $browsing.PhaseObservation.Viewport
                foreach ($property in @('WidthDip', 'HeightDip', 'DpiScaleX', 'DpiScaleY', 'WidthPhysical', 'HeightPhysical')) {
                    if ((Get-Timing $viewport.$property) -le 0) { throw 'InvalidReport' }
                }
                $viewportKey = @('WidthDip', 'HeightDip', 'DpiScaleX', 'DpiScaleY', 'WidthPhysical', 'HeightPhysical') | ForEach-Object { $_ + '=' + $viewport.$_ }
                $viewportKey = $viewportKey -join ';'
                if ($null -eq $expectedViewport) { $expectedViewport = $viewportKey }
                elseif ($expectedViewport -ne $viewportKey) { throw 'ViewportChanged' }
                $run.Success = $true
            }
            catch { $run.Failure = Get-StableFailure $_; throw }
            finally {
                Write-SafeJson @{ SchemaVersion = 1; Success = $false; Status = 'InProgress'; Runs = $runs.ToArray() } (Join-Path $destination 'index.json')
            }
        }
    }
    Assert-NoViewer $viewerNames
    foreach ($label in @('A', 'B')) {
        $after = Get-BinaryIdentity $executables[$label]
        if ($after.AssemblySetSha256 -ne $identities[$label].AssemblySetSha256 -or $after.ExecutableSha256 -ne $identities[$label].ExecutableSha256) { throw 'BinaryChanged' }
    }
    $success = $true
}
catch { $failure = Get-StableFailure $_ }
finally {
    if ($temporary -and [IO.Directory]::Exists($temporary)) {
        try { [IO.Directory]::Delete($temporary, $true) }
        catch { $success = $false; if (-not $failure) { $failure = Get-StableFailure $_ } }
    }
    if ($destination) {
        try {
            Write-SafeJson ([pscustomobject]@{
                SchemaVersion = 1; Success = $success; Failure = $failure; MeasuredAtUtc = [DateTime]::UtcNow.ToString('o')
                OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
                Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
                ProcessorCount = [Environment]::ProcessorCount
                RequestedPairs = $Pairs; WarmupPairs = $WarmupPairs
                BinaryIdentities = $identities; Runs = $runs.ToArray()
                Statistics = Get-ComparisonStatistics $runs.ToArray()
                Protocol = @{ Revision = 2; InputCount = 8; Dimensions = 'Alternating 64x64 and 2048x2048 PNG'; StartupSamplesPerRun = 1
                    NeighborSwitches = 8; RapidBurst = 4; RefreshEvery = 4; DetailEvery = 3
                    ObservationSeconds = $ObservationSeconds; IdleSeconds = $IdleSeconds; TimeoutSeconds = $TimeoutSeconds }
                Definitions = @{
                    A = 'Baseline binary'; B = 'Candidate binary'; Ratio = 'Candidate / baseline; null when baseline is zero.'
                    Median = 'Median of successful measured runs; warmups excluded. Switch/detail metrics are per-run means.'
                    FirstRequest = 'Minimum positive OpenRequested RequestId; first paint/navigation/detail SinceRequestMs for that request.'
                    RequestedDetail = 'Mean of two Actual Size demand-to-RequiredDetailPainted intervals after switches 3 and 6: one 2048x2048 and one 64x64 PNG, not total time since file open.'
                    Startup = 'Existing driver: process start to window handle + input idle, with no image argument.'
                    Provenance = 'Driver Commit and WorkingTreeDirty describe the script checkout; binary identity comes from ProductVersion and assembly hashes.'
                    RawReports = 'Independent driver JSON with all original fields retained except path/exception redaction; absent reports are explicit failure envelopes.'
                    Foreground = 'Hidden PowerShell children; checks arm after first activation. Any observed loss before the explicit post-measurement completion marker fails. Marker precedes normal close; raw JSON does not end checks. No duration tolerance, focus injection or window geometry changes. Diagnostic times use the outer observer clock.'
                }
                Limitations = 'Same-machine sequential observations, not P95 or a cold-start threshold. No cache eviction, forced GC, user images or modified viewer paths. Phases use the existing observation clock, not process creation; paint is not display scanout. Failed or incomplete comparisons are not acceptance results. Foreground sampling cannot detect every transient focus change.'
            }) (Join-Path $destination 'index.json')
        }
        catch { $success = $false; $failure = Get-StableFailure $_ }
    }
    if ($ownsMutex) { $mutex.ReleaseMutex() }
    if ($mutex) { $mutex.Dispose() }
}
if (-not $success) { Write-Output "Static comparison failed: $failure"; exit 1 }
Write-Output "Static comparison completed: $Pairs measured pairs; $WarmupPairs warmup pairs excluded. See index.json."
