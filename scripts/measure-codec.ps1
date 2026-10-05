#Requires -Version 7.0
param(
    [string[]]$ImagePaths,
    [string]$OutputPath = "artifacts/codec-observation/results.json",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$repository = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repository "benchmarks/ModernImageViewer.CodecProbe/ModernImageViewer.CodecProbe.csproj"
$runtimeDirectory = Join-Path $repository "benchmarks/ModernImageViewer.CodecProbe/bin/Release/net10.0-windows10.0.19041.0"
if (-not $NoBuild) {
    & dotnet build $project --configuration Release
    if ($LASTEXITCODE -ne 0) { throw "Codec probe build failed: $LASTEXITCODE" }
}
$probe = Join-Path $runtimeDirectory "ModernImageViewer.CodecProbe.dll"
if (-not (Test-Path $probe -PathType Leaf)) { throw "Build the Release codec probe before using -NoBuild." }
$dotnetPath = (Get-Command dotnet -CommandType Application).Source

function Invoke-Probe([string[]]$ProbeArguments) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new($dotnetPath)
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    $startInfo.ArgumentList.Add($probe)
    foreach ($argument in $ProbeArguments) { $startInfo.ArgumentList.Add($argument) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    try {
        [void]$process.Start()
        $stdout = $process.StandardOutput.ReadToEndAsync()
        $stderr = $process.StandardError.ReadToEndAsync()
        if (-not $process.WaitForExit(60000)) {
            $process.Kill($true)
            $process.WaitForExit()
            throw "Codec probe exceeded 60 seconds."
        }
        $out = $stdout.GetAwaiter().GetResult()
        $err = $stderr.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) { throw "Codec probe failed ($($process.ExitCode)): $err" }
        return $out
    }
    finally { $process.Dispose() }
}

$usesGeneratedSample = -not $ImagePaths
if ($usesGeneratedSample) {
    $sample = Join-Path $repository "artifacts/codec-observation/generated-100mp-rgb.png"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($sample)) | Out-Null
    if (-not (Test-Path $sample -PathType Leaf)) {
        Invoke-Probe @("generate-png", $sample) | Out-Null
    }
    $ImagePaths = @($sample)
}
$observations = [Collections.Generic.List[object]]::new()
foreach ($path in $ImagePaths) {
    $resolved = (Resolve-Path $path).Path
    $hash = (Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash
    foreach ($mode in @("preview", "thumbnail")) {
        $observation = Invoke-Probe @("decode", $resolved, $mode) | ConvertFrom-Json
        if ($usesGeneratedSample -and ($observation.SourceWidth -ne 10000 -or $observation.SourceHeight -ne 10000)) {
            throw 'The default generated sample was replaced and is no longer 10000 x 10000. Remove or select that sample explicitly.'
        }
        if ((Get-FileHash -LiteralPath $resolved -Algorithm SHA256).Hash -ne $hash) {
            throw 'The sample changed during observation; the measurements cannot be tied to its original hash.'
        }
        $observation | Add-Member -NotePropertyName SampleFileName -NotePropertyValue ([IO.Path]::GetFileName($resolved))
        $observation | Add-Member -NotePropertyName SampleSha256 -NotePropertyValue $hash
        $observation | Add-Member -NotePropertyName SampleFileBytes -NotePropertyValue ((Get-Item -LiteralPath $resolved).Length)
        $observations.Add($observation)
    }
}
$cpu = $null
$ram = $null
$hardwareNote = "CIM hardware metadata"
try {
    $cpu = @(Get-CimInstance Win32_Processor | Select-Object -ExpandProperty Name)
    $ram = (Get-CimInstance Win32_ComputerSystem).TotalPhysicalMemory
}
catch {
    $hardwareNote = "CIM hardware metadata unavailable: $($_.Exception.Message)"
    Write-Warning $hardwareNote
}
$commit = & git -C $repository rev-parse HEAD
$dirty = [bool](& git -C $repository status --porcelain)
$destination = [IO.Path]::GetFullPath($OutputPath)
[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
[pscustomobject]@{
    MeasuredAtUtc = [DateTime]::UtcNow.ToString("o")
    Commit = $commit
    WorkingTreeDirty = $dirty
    CpuModel = $cpu
    PhysicalMemoryBytes = $ram
    HardwareMetadataNote = $hardwareNote
    Configuration = "Release, framework-dependent, isolated codec process; not application startup or UI first frame"
    SamplesPerMode = 1
    Generated100MpSample = $usesGeneratedSample
    Generator = if ($usesGeneratedSample) {
        "10000x10000 RGB8 PNG streamed in a separate process; 30,001-byte row and 64KiB IDAT buffer, temporary compressed file"
    } else { $null }
    Observations = $observations
    Limitations = "Single observation; process counters include managed/runtime/native memory and do not isolate native allocation. " +
        "No forced GC, cancellation/recovery soak, WPF rendering, cold-start controls or P95 claim. Generated PNG is not representative of all camera/codec inputs."
} | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $destination -Encoding utf8
$observations | Format-Table Mode, SourceWidth, SourceHeight, OutputWidth, OutputHeight, OutputBytes, ElapsedMs, PeakWorkingSetBytes
