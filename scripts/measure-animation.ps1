#Requires -Version 7.0
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$AppPath,
    [string]$FixtureDirectory,
    [string[]]$ImagePaths,
    [ValidateRange(1, 1000)][int]$Loops = 1,
    [ValidateRange(0, 3600)][int]$LongSeconds = 0,
    [ValidateRange(10, 14400)][int]$TimeoutSeconds = 240,
    [switch]$RequireInstrumentation,
    [string]$OutputPath = 'artifacts/animation-observation/results.json'
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'Animation observation requires Windows.' }
if (-not $FixtureDirectory -and -not $ImagePaths) { throw 'Provide FixtureDirectory or explicit ImagePaths.' }
if ($FixtureDirectory -and $ImagePaths) { throw 'Use either FixtureDirectory or ImagePaths.' }
$executable = (Resolve-Path -LiteralPath $AppPath).Path
$destination = [IO.Path]::GetFullPath($OutputPath)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ModernImageViewer-animation-' + [Guid]::NewGuid().ToString('N'))
$process = [Diagnostics.Process]::new()
$watch = [Diagnostics.Stopwatch]::new()
$samples = [Collections.Generic.List[object]]::new()
$droppedSamples = 0
$started = $false
$phases = $null
$failure = $null
$passed = $false
$inputCount = 0
$inputKinds = @()

try {
    if ($FixtureDirectory) {
        $fixtures = (Resolve-Path -LiteralPath $FixtureDirectory).Path
        # Negative codec fixtures belong in codec tests, not the successful UI playback driver.
        $inputs = @(Get-ChildItem -LiteralPath $fixtures -File | Where-Object {
            $_.Name -like 'animation*' -and $_.Extension.ToLowerInvariant() -in @('.gif', '.webp', '.tif', '.tiff', '.png', '.jpg', '.jpeg', '.bmp') -and
            $_.BaseName -notmatch '(?i)corrupt|truncat|invalid|malformed|broken|damaged|oversiz|overlimit|too[-_]?large|excessive'
        } | Sort-Object Name | Select-Object -ExpandProperty FullName)
        $static = @(Get-ChildItem -LiteralPath $fixtures -File | Where-Object {
            $_.Extension.ToLowerInvariant() -in @('.png', '.jpg', '.jpeg', '.bmp') -and
            $_.BaseName -notmatch '(?i)corrupt|truncat|invalid|malformed|broken|damaged|oversiz|overlimit|too[-_]?large|excessive'
        } | Sort-Object Name | Select-Object -First 1 -ExpandProperty FullName)
        $inputs = @($inputs + $static | Select-Object -Unique)
    }
    else { $inputs = @($ImagePaths | ForEach-Object { (Resolve-Path -LiteralPath $_).Path } | Select-Object -Unique) }
    $inputCount = $inputs.Count
    $inputKinds = @($inputs | ForEach-Object { [IO.Path]::GetExtension($_).ToLowerInvariant() } | Sort-Object -Unique)
    if ($inputCount -lt 4 -or '.gif' -notin $inputKinds -or '.webp' -notin $inputKinds -or
        ('.tif' -notin $inputKinds -and '.tiff' -notin $inputKinds) -or
        -not @($inputKinds | Where-Object { $_ -in @('.png', '.jpg', '.jpeg', '.bmp') }).Count) {
        throw 'Inputs must include animation GIF, animation WebP, multipage TIFF and a static image.'
    }
    $inputDirectory = Join-Path $temporary 'inputs'
    [IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
    $copies = @(for ($index = 0; $index -lt $inputs.Count; $index++) {
        # F5 and any ordinary browse enumeration stay in this exclusively owned directory.
        $copy = Join-Path $inputDirectory ('input-{0:D4}{1}' -f $index, [IO.Path]::GetExtension($inputs[$index]))
        Copy-Item -LiteralPath $inputs[$index] -Destination $copy
        $copy
    })
    $phaseOutput = Join-Path $temporary 'phases.json'
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.ArgumentList.Add($copies[0])
    # Avoid inheriting another opt-in driver and running two drivers in one window.
    foreach ($name in @('MIV_BROWSING_OBSERVATION_OUTPUT', 'MIV_STARTUP_OBSERVATION_OUTPUT')) { [void]$info.Environment.Remove($name) }
    $info.Environment['MIV_ANIMATION_OBSERVATION_OUTPUT'] = $phaseOutput
    $info.Environment['MIV_ANIMATION_OBSERVATION_INPUTS'] = ConvertTo-Json -InputObject $copies -Compress
    $info.Environment['MIV_ANIMATION_LOOPS'] = $Loops.ToString([Globalization.CultureInfo]::InvariantCulture)
    $info.Environment['MIV_ANIMATION_LONG_SECONDS'] = $LongSeconds.ToString([Globalization.CultureInfo]::InvariantCulture)
    if (@(Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($executable)) -ErrorAction SilentlyContinue).Count) {
        throw 'ViewerAlreadyRunning'
    }
    $process.StartInfo = $info
    $watch.Start()
    $started = $process.Start()
    if (-not $started) { throw 'Observation process did not start.' }
    while (-not $process.HasExited) {
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'ObservationTimeout' }
        $process.Refresh()
        if ($process.HasExited) { break }
        try {
            $sample = [pscustomobject]@{
                ElapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
                WorkingSetBytes = $process.WorkingSet64
                PrivateBytes = $process.PrivateMemorySize64
                HandleCount = $process.HandleCount
                Threads = $process.Threads.Count
            }
            if ($samples.Count -lt 4096) { $samples.Add($sample) } else { $droppedSamples++ }
        }
        catch [InvalidOperationException] { if (-not $process.HasExited) { throw } }
        Start-Sleep -Milliseconds 250
    }
    if ([IO.File]::Exists($phaseOutput)) { $phases = Get-Content -LiteralPath $phaseOutput -Raw | ConvertFrom-Json }
    if ($null -eq $phases) { throw 'MissingObservationReportOrCloseHook' }
    if ($process.ExitCode -ne 0) { throw 'AbnormalProcessExit' }
    if (-not $phases.Success) { throw 'AnimationDriverFailed' }
    if ($phases.CompletedLoops -ne $Loops) { throw 'IncompleteLoops' }
    foreach ($stage in @('WindowVisible', 'AnimationGif', 'AnimationWebp', 'PagesSeekAndDetail', 'PlaybackAdvanced',
        'UserPause', 'UserResume', 'AnimationSeek', 'BackgroundPause', 'VisibleResume', 'UserPausePreserved', 'Restart',
        'ViewportAndFullscreen', 'RefreshF5', 'RapidSwitch', 'StaticResourcesReleased', 'StaticDrain',
        'BeforeRelease', 'ImageResourcesDrained', 'ReleasedIdle', 'BeforeClose')) {
        if ($stage -notin $phases.Coverage) { throw 'MissingRequiredCoverage' }
    }
    if ($RequireInstrumentation -and -not $phases.InstrumentationComplete) { throw 'IncompleteResourceInstrumentation' }
    $passed = $true
}
catch {
    # Export stable categories only; PowerShell exception text can contain input paths.
    $known = @('ObservationTimeout', 'MissingObservationReportOrCloseHook', 'AbnormalProcessExit', 'AnimationDriverFailed',
        'IncompleteLoops', 'MissingRequiredCoverage', 'IncompleteResourceInstrumentation', 'ViewerAlreadyRunning')
    $failure = if ($_.Exception.Message -in $known) { $_.Exception.Message } else { $_.Exception.GetType().Name }
}
finally {
    $forcedTermination = $false
    if ($started -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) {
            $forcedTermination = $true
            $process.Kill($true)
            [void]$process.WaitForExit(5000)
        }
    }
    if ($null -eq $phases -and $phaseOutput -and [IO.File]::Exists($phaseOutput)) {
        try { $phases = Get-Content -LiteralPath $phaseOutput -Raw | ConvertFrom-Json } catch { }
    }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    [pscustomobject]@{
        SchemaVersion = 1
        Success = $passed
        Failure = $failure
        MeasuredAtUtc = [DateTime]::UtcNow.ToString('o')
        OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        ProcessorCount = [Environment]::ProcessorCount
        ExecutableProductVersion = $version.ProductVersion
        InputCount = $inputCount
        InputKinds = $inputKinds
        Loops = $Loops
        LongSeconds = $LongSeconds
        RequiredInstrumentation = [bool]$RequireInstrumentation
        ForcedTermination = $forcedTermination
        PhaseObservation = $phases
        ProcessSamples = $samples
        DroppedProcessSamples = $droppedSamples
        Peaks = [pscustomobject]@{
            WorkingSetBytes = ($samples.WorkingSetBytes | Measure-Object -Maximum).Maximum
            PrivateBytes = ($samples.PrivateBytes | Measure-Object -Maximum).Maximum
            HandleCount = ($samples.HandleCount | Measure-Object -Maximum).Maximum
        }
        Limitations = 'Single Windows process; sampled WS/private/handles are observations, not pixel budget caps or proof of no leaks. Inputs copied into an owned temporary directory; originals untouched. No forced GC, global input injection, paths or file names in this report. Null resource counters mean uninstrumented. A forced process termination never proves clean close.'
    } | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $destination -Encoding utf8
    $process.Dispose()
    if ([IO.Directory]::Exists($temporary)) { [IO.Directory]::Delete($temporary, $true) }
}
if (-not $passed) { throw "Animation observation failed ($failure)." }
Write-Output "Animation observation passed: $Loops loops; $LongSeconds seconds sustained playback; close snapshot recorded."
