#Requires -Version 7.0
# Only extract the two logging helpers: never execute installer or registry code.
$ErrorActionPreference = 'Stop'
foreach ($entry in @(
    @{ File = 'check-installer.ps1'; Function = 'Write-InstallerCheckpoint' },
    @{ File = 'invoke-installer-check.ps1'; Function = 'Write-SmokeOutput' }
)) {
    $tokens = $null
    $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile(
        (Join-Path $PSScriptRoot $entry.File), [ref]$tokens, [ref]$errors)
    if ($errors.Count) { throw 'InstallerScriptParseFailed' }
    $functionName = $entry.Function
    $function = $ast.Find({ param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $functionName
    }, $false)
    if ($null -eq $function) { throw 'InstallerObservationFunctionMissing' }
    . ([scriptblock]::Create($function.Extent.Text))
}
$logs = Join-Path ([IO.Path]::GetTempPath()) ('MIV-Checkpoint-Test-' + [Guid]::NewGuid().ToString('N'))
$outputPath = Join-Path $logs 'worker-output.log'
try {
    $result = & { Write-InstallerCheckpoint 'InitialInstall'; 42 }
    if ($result -cne 42) { throw 'CheckpointPollutedSuccessOutput' }
    Write-InstallerCheckpoint 'InitialActivation'
    $checkpointText = Get-Content (Join-Path $logs 'checkpoint.json') -Raw
    $checkpoint = $checkpointText | ConvertFrom-Json
    # ConvertFrom-Json can materialize ISO timestamps in the local time zone.
    $document = [Text.Json.JsonDocument]::Parse($checkpointText)
    try { $checkpointTime = [DateTimeOffset]::Parse($document.RootElement.GetProperty('AtUtc').GetString()) }
    finally { $document.Dispose() }
    if ($checkpoint.SchemaVersion -ne 1 -or $checkpoint.Stage -cne 'InitialActivation' -or
        ($checkpoint.PSObject.Properties.Name -join ',') -cne 'SchemaVersion,Stage,AtUtc' -or
        $checkpointTime.Offset -ne [TimeSpan]::Zero -or
        (Test-Path (Join-Path $logs 'checkpoint.json.pending'))) { throw 'InvalidCheckpointSnapshot' }

    $messages = @(& { Write-SmokeOutput 'MIV_INSTALLER_CHECKPOINT:InitialActivation' } 6>&1 | ForEach-Object { $_.ToString() })
    if ($messages -cnotcontains '::notice title=Installer checkpoint::InitialActivation' -or
        (Get-Content $outputPath -Raw) -notmatch 'MIV_INSTALLER_CHECKPOINT:InitialActivation') { throw 'CheckpointNotForwarded' }
    $messages = @(& { Write-SmokeOutput 'MIV_INSTALLER_CHECKPOINT:Bad%0APhase' } 6>&1 | ForEach-Object { $_.ToString() })
    if (@($messages | Where-Object { $_.StartsWith('::notice', [StringComparison]::Ordinal) }).Count) {
        throw 'InvalidCheckpointBecameAnnotation'
    }
    $rejected = $false
    try { Write-InstallerCheckpoint 'Bad%0APhase' } catch { $rejected = $true }
    if (-not $rejected) { throw 'InvalidCheckpointAccepted' }
    if ((Get-Content (Join-Path $logs 'checkpoint.json') -Raw | ConvertFrom-Json).Stage -cne 'InitialActivation') {
        throw 'InvalidCheckpointChangedSnapshot'
    }

    $locked = [IO.File]::Open((Join-Path $logs 'checkpoint.json'), [IO.FileMode]::Open,
        [IO.FileAccess]::Read, [IO.FileShare]::None)
    try { $result = & { Write-InstallerCheckpoint 'Upgrade'; 42 } }
    finally { $locked.Dispose() }
    if ($result -cne 42 -or (Get-Content (Join-Path $logs 'checkpoint.json') -Raw | ConvertFrom-Json).Stage -cne 'InitialActivation') {
        throw 'OptionalCheckpointFailureChangedValidation'
    }
    Write-InstallerCheckpoint 'Upgrade'
    if ((Get-Content (Join-Path $logs 'checkpoint.json') -Raw | ConvertFrom-Json).Stage -cne 'Upgrade' -or
        (Test-Path (Join-Path $logs 'checkpoint.json.pending'))) { throw 'CheckpointDidNotRecover' }
}
finally {
    if ([IO.Directory]::Exists($logs)) { [IO.Directory]::Delete($logs, $true) }
}
Write-Output 'Installer checkpoint regressions passed; no installer, viewer or registry operations.'
