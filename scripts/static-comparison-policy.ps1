#Requires -Version 7.0
function Get-StaticComparisonDisposition($Report, [int]$ExitCode) {
    if ($Report.SchemaVersion -ne 1 -or $Report.Success -isnot [bool] -or
        $Report.RequestedPairs -lt 1 -or $null -eq $Report.Runs -or $null -eq $Report.Statistics) {
        throw 'InvalidComparisonReport'
    }
    if ($ExitCode -eq 1 -and $Report.Success -eq $false -and
        $Report.Failure -in @('ForegroundWindowLost', 'ForegroundNotObserved')) {
        $failed = @($Report.Runs | Where-Object { $_.Success -eq $false })
        if ($failed.Count -ne 1 -or $failed[0].Failure -ne $Report.Failure) { throw 'InvalidComparisonReport' }
        $drivers = @($failed[0].DriverStatus.Values)
        if (-not $drivers.Count -or @($drivers | Where-Object {
            $_.RawReportAvailable -ne $true -or
            ($_.Success -ne $true -and $_.Failure -notin @('ForegroundWindowLost', 'ForegroundNotObserved'))
        }).Count) { throw 'InvalidComparisonReport' }
        return 'Inconclusive'
    }
    if ($ExitCode -ne 0 -or $Report.Success -ne $true -or $Report.Failure -or
        $Report.Statistics.CompletePairs -ne $Report.RequestedPairs) { throw 'ComparisonFailed' }
    $measured = @($Report.Runs | Where-Object { $_.Warmup -eq $false })
    if ($measured.Count -ne 2 * $Report.RequestedPairs -or @($Report.Runs | Where-Object {
        $_.Success -ne $true -or $_.Failure -or $null -eq $_.Metrics -or
        @($_.DriverStatus.Values).Count -ne 2 -or @($_.DriverStatus.Values | Where-Object {
            $_.Success -ne $true -or $_.Failure -or $_.RawReportAvailable -ne $true
        }).Count
    }).Count) { throw 'InvalidComparisonReport' }
    foreach ($pair in 1..$Report.RequestedPairs) {
        $versions = @($measured | Where-Object { $_.Pair -eq $pair } | ForEach-Object { $_.Version } | Sort-Object)
        if (($versions -join ',') -cne 'A,B') { throw 'InvalidComparisonReport' }
    }
    $ratios = @($Report.Statistics.PairRatios)
    if ($ratios.Count -ne $Report.RequestedPairs) { throw 'InvalidComparisonReport' }
    foreach ($metric in @('StartupWindowInputIdleMs', 'FirstRecognizableMs', 'NavigationAvailableMs',
        'NeighborSwitchMeanMs', 'FirstRequiredDetailMs', 'RequestedDetailMeanMs')) {
        $values = @(foreach ($pair in $ratios) {
            $value = $pair.CandidateOverBaseline.$metric
            if ($null -eq $value -or $value -is [string] -or $value -is [bool] -or
                -not [double]::IsFinite([double]$value) -or $value -le 0) { throw 'InvalidComparisonReport' }
            [double]$value
        })
        $values = @($values | Sort-Object)
        $middle = [int][Math]::Floor($values.Count / 2)
        $median = if ($values.Count % 2) { $values[$middle] } else { ($values[$middle - 1] + $values[$middle]) / 2 }
        if ($median -gt 1.1) { throw "PerformanceReviewRequired:$metric" }
    }
    return 'Valid'
}
