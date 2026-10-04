#Requires -Version 7.0
param([Parameter(Mandatory)][string]$AppPath)

$ErrorActionPreference = "Stop"
$executable = (Resolve-Path $AppPath).Path
$directory = Join-Path ([IO.Path]::GetTempPath()) ("ModernImageViewer-activation-" + [Guid]::NewGuid().ToString("N"))
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

function Start-Viewer([string[]]$ImagePaths) {
    $info = [Diagnostics.ProcessStartInfo]::new($executable)
    $info.UseShellExecute = $false
    foreach ($imagePath in $ImagePaths) { $info.ArgumentList.Add($imagePath) }
    $process = [Diagnostics.Process]::new()
    $process.StartInfo = $info
    if (-not $process.Start()) { throw "Published application did not start." }
    $processes.Add($process)
    return $process
}

function Wait-ForImage([Diagnostics.Process]$Process, [string]$FileName) {
    $watch = [Diagnostics.Stopwatch]::StartNew()
    while ($watch.Elapsed.TotalSeconds -lt 20) {
        $Process.Refresh()
        if ($Process.HasExited) { throw "Primary application exited before showing an image." }
        if ($Process.MainWindowTitle.StartsWith($FileName + " — ", [StringComparison]::Ordinal)) { return }
        Start-Sleep -Milliseconds 50
    }
    throw "The expected image did not appear in the primary window."
}

function Assert-Forwarded([Diagnostics.Process]$Process) {
    if (-not $Process.WaitForExit(10000)) { throw "Secondary application did not exit after forwarding." }
    if ($Process.ExitCode -ne 0) { throw "Secondary application could not forward its request." }
}

try {
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    # A generated 1x1 RGBA PNG with valid chunk checksums; no external sample download.
    $png = [Convert]::FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR4nGNImfbrPwAGSgL09Lc0kwAAAABJRU5ErkJggg==")
    $firstName = "初次打开 & (1).png"
    $secondName = "后续打开 & (2).png"
    $first = Join-Path $directory $firstName
    $second = Join-Path $directory $secondName
    [IO.File]::WriteAllBytes($first, $png)
    [IO.File]::WriteAllBytes($second, $png)

    $primary = Start-Viewer @($first)
    Wait-ForImage $primary $firstName
    $secondary = Start-Viewer @($second)
    Assert-Forwarded $secondary
    Wait-ForImage $primary $secondName
    $activation = Start-Viewer @()
    Assert-Forwarded $activation
    Wait-ForImage $primary $secondName
    Write-Output "Published application opened two images in one primary window; secondary requests exited successfully."
}
finally {
    foreach ($process in $processes) {
        try {
            if (-not $process.HasExited) {
                $process.CloseMainWindow() | Out-Null
                if (-not $process.WaitForExit(2000)) { $process.Kill($true); $process.WaitForExit(2000) | Out-Null }
            }
        }
        finally { $process.Dispose() }
    }
    if ([IO.Directory]::Exists($directory)) { [IO.Directory]::Delete($directory, $true) }
}
