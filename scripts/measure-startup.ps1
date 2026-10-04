#Requires -Version 7.0
param(
    [Parameter(Mandatory)][string]$AppPath,
    [ValidateRange(1, 20)][int]$Iterations = 3,
    [string]$ImagePath,
    [ValidateRange(1, 30)][int]$ObservationSeconds = 3,
    [string]$OutputPath = "startup-results.json"
)

$ErrorActionPreference = "Stop"
$executable = (Resolve-Path $AppPath).Path
$samples = [System.Collections.Generic.List[object]]::new()

for ($iteration = 1; $iteration -le $Iterations; $iteration++) {
    $startInfo = [System.Diagnostics.ProcessStartInfo]::new($executable)
    $startInfo.UseShellExecute = $false
    if ($ImagePath) { $startInfo.ArgumentList.Add((Resolve-Path $ImagePath).Path) }
    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $startInfo
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    $started = $false
    try {
        $started = $process.Start()
        $ready = $false
        while ($watch.Elapsed.TotalSeconds -lt 20) {
            $process.Refresh()
            if ($process.HasExited) { throw "Application exited before its window became ready." }
            if ($process.MainWindowHandle -ne [IntPtr]::Zero -and
                $process.MainWindowTitle -match 'Modern Image Viewer|现代图片查看器' -and
                $process.WaitForInputIdle(50)) {
                $ready = $true
                break
            }
            Start-Sleep -Milliseconds 10
        }
        if (-not $ready) { throw "Application did not reach window/input-idle readiness within 20 seconds." }
        $watch.Stop()
        Start-Sleep -Seconds $ObservationSeconds
        $process.Refresh()
        $samples.Add([pscustomobject]@{
            Sample = $iteration
            WindowInputIdleMs = [Math]::Round($watch.Elapsed.TotalMilliseconds, 1)
            WorkingSetMiB = [Math]::Round($process.WorkingSet64 / 1MB, 1)
            PeakWorkingSetMiB = [Math]::Round($process.PeakWorkingSet64 / 1MB, 1)
            ObservationSeconds = $ObservationSeconds
        })
    }
    finally {
        if ($started -and -not $process.HasExited) {
            [void]$process.CloseMainWindow()
            if (-not $process.WaitForExit(5000)) { $process.Kill(); $process.WaitForExit() }
        }
        $process.Dispose()
    }
}

[pscustomobject]@{
    MeasuredAtUtc = [DateTime]::UtcNow.ToString("o")
    OS = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
    ProcessorCount = [Environment]::ProcessorCount
    Architecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
    OpenedImage = [bool]$ImagePath
    Samples = $samples
    Note = "Window handle + input-idle readiness; not first-frame timing. Image decoding may continue during observation."
} | ConvertTo-Json -Depth 4 | Set-Content -Path $OutputPath -Encoding utf8
$samples | Format-Table
