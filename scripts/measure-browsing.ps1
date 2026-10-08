#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$AppPath,
    [string]$ImageDirectory,
    [ValidateRange(2, 1000)][int]$GeneratedImageCount = 16,
    [ValidateRange(0, 1000)][int]$NeighborSwitches = 8,
    [ValidateRange(0, 20)][int]$RapidBurst = 4,
    [ValidateRange(0, 1000)][int]$RefreshEvery = 4,
    [ValidateRange(0, 1000)][int]$DetailEvery = 0,
    [ValidateRange(1, 120)][int]$IdleSeconds = 5,
    [ValidateRange(10, 3600)][int]$TimeoutSeconds = 180,
    [ValidateSet('Auto', 'FrameworkDependent', 'SelfContained')][string]$BuildKind = 'Auto',
    [string]$OutputPath = "artifacts/browsing-observation/results.json",
    [string]$ObservationCompleteSignalPath
)

$ErrorActionPreference = 'Stop'
$executable = (Resolve-Path -LiteralPath $AppPath).Path
$destination = [IO.Path]::GetFullPath($OutputPath)
$temporary = Join-Path ([IO.Path]::GetTempPath()) ('ModernImageViewer-browsing-' + [Guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($temporary) | Out-Null
$phaseOutput = Join-Path $temporary 'phases.json'
$samples = [Collections.Generic.List[object]]::new()
$process = [Diagnostics.Process]::new()
$watch = [Diagnostics.Stopwatch]::new()
$started = $false

function Read-ResourceSample([string]$Stage) {
    $process.Refresh()
    if ($process.HasExited) { throw 'Observed viewer exited before measurement completed; close other viewer instances first.' }
    $samples.Add([pscustomobject]@{
        Stage = $Stage
        ElapsedMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
        WorkingSetBytes = $process.WorkingSet64
        PrivateBytes = $process.PrivateMemorySize64
        HandleCount = $process.HandleCount
        Threads = $process.Threads.Count
    })
}

function Get-IdleTrend([string]$Property) {
    $idle = @($samples | Where-Object Stage -eq 'Idle')
    if ($idle.Count -lt 2) { return $null }
    $meanTime = ($idle.ElapsedMs | Measure-Object -Average).Average / 1000
    $meanValue = ($idle.$Property | Measure-Object -Average).Average
    $numerator = 0.0
    $denominator = 0.0
    foreach ($sample in $idle) {
        $x = $sample.ElapsedMs / 1000 - $meanTime
        $numerator += $x * ($sample.$Property - $meanValue)
        $denominator += $x * $x
    }
    [pscustomobject]@{
        First = $idle[0].$Property
        Last = $idle[-1].$Property
        Minimum = ($idle.$Property | Measure-Object -Minimum).Minimum
        Maximum = ($idle.$Property | Measure-Object -Maximum).Maximum
        UnitsPerSecond = if ($denominator -gt 0) { [Math]::Round($numerator / $denominator, 3) } else { $null }
    }
}

try {
    $generated = -not $ImageDirectory
    if ($generated) {
        $inputDirectory = Join-Path $temporary 'images'
        [IO.Directory]::CreateDirectory($inputDirectory) | Out-Null
        Add-Type -AssemblyName System.Drawing.Common
        # Alternate small/large real PNGs without a full-image managed byte-array copy.
        for ($index = 0; $index -lt $GeneratedImageCount; $index++) {
            $edge = if ($index % 2 -eq 0) { 64 } else { 2048 }
            $bitmap = [Drawing.Bitmap]::new($edge, $edge)
            $graphics = [Drawing.Graphics]::FromImage($bitmap)
            try {
                $graphics.Clear([Drawing.Color]::FromArgb(255, ($index * 17) % 256, ($index * 29) % 256, ($index * 41) % 256))
                $bitmap.Save((Join-Path $inputDirectory ('sample-{0:D4}.png' -f $index)), [Drawing.Imaging.ImageFormat]::Png)
            }
            finally { $graphics.Dispose(); $bitmap.Dispose() }
        }
    }
    else { $inputDirectory = (Resolve-Path -LiteralPath $ImageDirectory).Path }
    $supported = @('.jpg', '.jpeg', '.png', '.bmp', '.gif', '.tif', '.tiff', '.ico', '.webp', '.avif', '.heif', '.heic', '.jxr', '.wdp', '.hdp', '.svg', '.dng', '.cr2', '.cr3', '.nef', '.arw', '.raf', '.rw2', '.orf', '.pef')
    $inputs = @(Get-ChildItem -LiteralPath $inputDirectory -File | Where-Object { $_.Extension.ToLowerInvariant() -in $supported } | Sort-Object Name)
    if ($inputs.Count -lt 2) { throw 'Browsing observation requires at least two supported images.' }
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    $info.ArgumentList.Add($inputs[0].FullName)
    $info.Environment['MIV_BROWSING_OBSERVATION_OUTPUT'] = $phaseOutput
    $info.Environment['MIV_BROWSING_SWITCHES'] = $NeighborSwitches.ToString([Globalization.CultureInfo]::InvariantCulture)
    $info.Environment['MIV_BROWSING_RAPID_BURST'] = $RapidBurst.ToString([Globalization.CultureInfo]::InvariantCulture)
    $info.Environment['MIV_BROWSING_REFRESH_EVERY'] = $RefreshEvery.ToString([Globalization.CultureInfo]::InvariantCulture)
    $info.Environment['MIV_BROWSING_DETAIL_EVERY'] = $DetailEvery.ToString([Globalization.CultureInfo]::InvariantCulture)
    $process.StartInfo = $info
    $watch.Start()
    $started = $process.Start()
    if (-not $started) { throw 'Could not start browsing observation.' }
    while (-not [IO.File]::Exists($phaseOutput)) {
        Read-ResourceSample 'Browsing'
        if ($watch.Elapsed.TotalSeconds -ge $TimeoutSeconds) { throw 'Browsing observation exceeded its overall deadline.' }
        Start-Sleep -Milliseconds 250
    }
    $phases = Get-Content -LiteralPath $phaseOutput -Raw | ConvertFrom-Json
    if (-not $phases.Success) { throw "Browsing phase driver failed: $($phases.Failure)." }
    foreach ($name in @('WindowVisible', 'OpenRequested', 'FirstRecognizablePainted', 'NavigationAvailable')) {
        if ($name -notin $phases.Phases.Phase) { throw "Missing browsing phase: $name." }
    }
    if ('RequiredDetailPainted' -notin $phases.Phases.Phase -and 'DetailBudgetLimited' -notin $phases.Phases.Phase) {
        throw 'Missing required-detail completion or explicit budget-limit outcome.'
    }
    if ($NeighborSwitches -gt 0 -and ('NeighborSwitchCompleted' -notin $phases.Phases.Phase -or $phases.CompletedNeighborSwitches -ne $NeighborSwitches)) {
        throw 'Neighbor-switch phase or completion count is missing.'
    }
    $idleWatch = [Diagnostics.Stopwatch]::StartNew()
    do {
        Read-ResourceSample 'Idle'
        Start-Sleep -Milliseconds 250
    } while ($idleWatch.Elapsed.TotalSeconds -lt $IdleSeconds)
    Read-ResourceSample 'Idle'
    $repository = Split-Path $PSScriptRoot -Parent
    $cpu = $null
    $ram = $null
    $machineModel = $null
    $hardwareNote = 'CIM hardware metadata'
    try {
        $cpu = @(Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name)
        $computer = Get-CimInstance Win32_ComputerSystem
        $ram = $computer.TotalPhysicalMemory
        $machineModel = $computer.Model
    }
    catch { $hardwareNote = 'CIM hardware metadata unavailable' }
    $resolvedBuildKind = $BuildKind
    if ($BuildKind -eq 'Auto') {
        $runtimeConfig = [IO.Path]::ChangeExtension($executable, 'runtimeconfig.json')
        $resolvedBuildKind = 'Unknown'
        if ([IO.File]::Exists($runtimeConfig)) {
            $runtimeOptions = (Get-Content -LiteralPath $runtimeConfig -Raw | ConvertFrom-Json).runtimeOptions
            if ($runtimeOptions.includedFrameworks) { $resolvedBuildKind = 'SelfContained' }
            elseif ($runtimeOptions.framework -or $runtimeOptions.frameworks) { $resolvedBuildKind = 'FrameworkDependent' }
        }
    }
    $commit = & git -C $repository rev-parse HEAD
    $executableVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($executable)
    $dirty = [bool](& git -C $repository status --porcelain)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    [pscustomobject]@{
        MeasuredAtUtc = [DateTime]::UtcNow.ToString('o')
        Commit = $commit
        WorkingTreeDirty = $dirty
        OS = [Runtime.InteropServices.RuntimeInformation]::OSDescription
        ProcessorCount = [Environment]::ProcessorCount
        CpuModel = $cpu
        PhysicalMemoryBytes = $ram
        MachineModel = $machineModel
        HardwareMetadataNote = $hardwareNote
        BuildKind = $resolvedBuildKind
        ExecutableProductVersion = $executableVersion.ProductVersion
        ExecutableFileVersion = $executableVersion.FileVersion
        Architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        GeneratedSamples = $generated
        InputCount = $inputs.Count
        InputKinds = @($inputs.Extension | ForEach-Object { $_.ToLowerInvariant() } | Sort-Object -Unique)
        GeneratedDimensions = if ($generated) { 'Alternating 64x64 and 2048x2048 PNG' } else { $null }
        NeighborSwitches = $NeighborSwitches
        RapidBurst = $RapidBurst
        RefreshEvery = $RefreshEvery
        DetailEvery = $DetailEvery
        IdleSeconds = $IdleSeconds
        PhaseObservation = $phases
        Resources = $samples
        IdleWorkingSet = Get-IdleTrend 'WorkingSetBytes'
        IdlePrivateBytes = Get-IdleTrend 'PrivateBytes'
        IdleHandles = Get-IdleTrend 'HandleCount'
        Limitations = 'One opt-in normal viewer run, not P95. Resource counters include runtime/native allocations; idle slopes are observations, not proof of no leaks. No forced GC. No full paths or file names are exported. WindowVisible means WPF ContentRendered; paint callbacks are not display scanout. Commands use the normal browsing APIs with no system-wide input injection. Use a fixed Windows environment and identical generated count or controlled sample directory for comparisons.'
        StressExample = 'measure-browsing.ps1 -AppPath <viewer> -GeneratedImageCount 400 -NeighborSwitches 800 -RefreshEvery 50 -DetailEvery 25 -RapidBurst 20 -IdleSeconds 30 -TimeoutSeconds 1800'
    } | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $destination -Encoding utf8
    Write-Output "Observed $($phases.CompletedNeighborSwitches) neighbor switches across $($inputs.Count) inputs; resource and idle samples recorded. Single run, not P95."
    if ($ObservationCompleteSignalPath) {
        [IO.File]::WriteAllText([IO.Path]::GetFullPath($ObservationCompleteSignalPath), 'ObservationComplete')
    }
}
finally {
    if ($started -and -not $process.HasExited) {
        [void]$process.CloseMainWindow()
        if (-not $process.WaitForExit(5000)) { $process.Kill($true); [void]$process.WaitForExit(5000) }
    }
    $process.Dispose()
    if ([IO.Directory]::Exists($temporary)) { [IO.Directory]::Delete($temporary, $true) }
}
