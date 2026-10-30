param(
    [Parameter(Mandatory = $true)][string]$BaselinePath,
    [Parameter(Mandatory = $true)][string]$CurrentPath,
    [string]$OutputPath = "artifacts/performance/playback-comparison.json",
    [string]$ProfilePath = (Join-Path $PSScriptRoot "PlaybackPerformanceProfile.json"),
    [switch]$AllowDifferentQuality
)

$ErrorActionPreference = "Stop"
$baseline = Get-Content -Raw $BaselinePath | ConvertFrom-Json
$current = Get-Content -Raw $CurrentPath | ConvertFrom-Json
$profile = Get-Content -Raw $ProfilePath | ConvertFrom-Json
if ($baseline.schema -ne $current.schema -or $baseline.schema -ne "homeplayer.playback.performance.v1") {
    throw "Performance schemas do not match."
}
if ($baseline.media.sha256 -ne $current.media.sha256) {
    throw "A/B inputs must use the same media file."
}
$qualityEquivalent = $baseline.environment.quality -eq $current.environment.quality
if (-not $qualityEquivalent -and -not $AllowDifferentQuality) {
    throw "Quality paths differ; use -AllowDifferentQuality only when the output must record that difference explicitly."
}
if ($baseline.protocol.openObservation -ne $current.protocol.openObservation -or
    $baseline.protocol.seekObservation -ne $current.protocol.seekObservation) {
    throw "Observation semantics differ; compare the metrics only after aligning the protocol."
}

function Compare-Metric($before, $after, [string]$name) {
    $result = [ordered]@{ name = $name }
    foreach ($percentile in @("p50Milliseconds", "p95Milliseconds", "p99Milliseconds")) {
        $old = $before.$percentile
        $new = $after.$percentile
        $result[$percentile] = [ordered]@{
            baseline = $old
            current = $new
            changePercent = if ($null -ne $old -and $null -ne $new -and [double]$old -ne 0) {
                ([double]$new - [double]$old) / [double]$old * 100
            } else { $null }
        }
    }
    $result["count"] = [ordered]@{ baseline = $before.count; current = $after.count }
    $result["failures"] = [ordered]@{ baseline = $before.failures; current = $after.failures }
    $result["unknown"] = [ordered]@{ baseline = $before.unknown; current = $after.unknown }
    return $result
}

function Assert-AbsoluteBudget([string]$name, $actual, [double]$budget) {
    if ($null -eq $actual -or [double]$actual -gt $budget) {
        throw "$name exceeds the absolute budget: $actual > $budget"
    }
}
function Assert-RegressionBudget([string]$name, $before, $after, [double]$budgetPercent) {
    if ($null -eq $before -or $null -eq $after -or [double]$before -eq 0) {
        throw "$name cannot be evaluated because a percentile is missing."
    }
    $change = ([double]$after - [double]$before) / [double]$before * 100
    if ($change -gt $budgetPercent) {
        throw "$name exceeds the regression budget: $change% > $budgetPercent%"
    }
}
if ($current.firstDecodedFrame.failures -ne 0 -or $current.seekToTargetDecodedFrame.failures -ne 0) {
    throw "The current run contains failed samples and cannot pass the profile."
}
if ($current.protocol.openIterations -lt $profile.samples.processColdAndWarmOpens -or
    $current.protocol.seekIterations -lt $profile.samples.seeksPerScenario) {
    throw "The current run does not contain the required number of samples."
}
Assert-AbsoluteBudget "open P95" $current.open.p95Milliseconds $profile.absoluteBudgets.openP95Milliseconds
Assert-AbsoluteBudget "first decoded P95" $current.firstDecodedFrame.p95Milliseconds $profile.absoluteBudgets.firstDecodedP95Milliseconds
Assert-AbsoluteBudget "seek P95" $current.seekToTargetDecodedFrame.p95Milliseconds $profile.absoluteBudgets.seekP95Milliseconds
Assert-RegressionBudget "first decoded P95" $baseline.firstDecodedFrame.p95Milliseconds $current.firstDecodedFrame.p95Milliseconds $profile.regressionBudgetsPercent.firstDecodedP95
Assert-RegressionBudget "first decoded P99" $baseline.firstDecodedFrame.p99Milliseconds $current.firstDecodedFrame.p99Milliseconds $profile.regressionBudgetsPercent.firstDecodedP99
Assert-RegressionBudget "seek P95" $baseline.seekToTargetDecodedFrame.p95Milliseconds $current.seekToTargetDecodedFrame.p95Milliseconds $profile.regressionBudgetsPercent.seekP95
Assert-RegressionBudget "seek P99" $baseline.seekToTargetDecodedFrame.p99Milliseconds $current.seekToTargetDecodedFrame.p99Milliseconds $profile.regressionBudgetsPercent.seekP99
Assert-RegressionBudget "steady allocated bytes" $baseline.steady.allocatedBytes $current.steady.allocatedBytes $profile.regressionBudgetsPercent.steadyAllocatedBytes

$comparison = [ordered]@{
    schema = "homeplayer.playback.performance.comparison.v1"
    profile = $profile.profile
    acceptance = "Passed"
    recordedUtc = [DateTime]::UtcNow.ToString("o")
    mediaSha256 = $current.media.sha256
    qualityEquivalent = $qualityEquivalent
    baselineQuality = $baseline.environment.quality
    currentQuality = $current.environment.quality
    quality = $current.environment.quality
    metrics = @(
        Compare-Metric $baseline.firstDecodedFrame $current.firstDecodedFrame "firstDecodedFrame"
        Compare-Metric $baseline.seekToTargetDecodedFrame $current.seekToTargetDecodedFrame "seekToTargetDecodedFrame"
    )
    steady = [ordered]@{
        cpuUtilizationPercent = [ordered]@{
            baseline = $baseline.steady.cpuUtilizationPercent
            current = $current.steady.cpuUtilizationPercent
            changePercent = ($current.steady.cpuUtilizationPercent - $baseline.steady.cpuUtilizationPercent) / $baseline.steady.cpuUtilizationPercent * 100
        }
        allocatedBytes = [ordered]@{
            baseline = $baseline.steady.allocatedBytes
            current = $current.steady.allocatedBytes
            changePercent = ($current.steady.allocatedBytes - $baseline.steady.allocatedBytes) / $baseline.steady.allocatedBytes * 100
        }
        decodedFrames = [ordered]@{ baseline = $baseline.steady.decodedFrames; current = $current.steady.decodedFrames }
    }
    note = "Negative change percentages are improvements for latency, CPU and allocation. Unknown and failed samples remain explicit."
}
if (-not [IO.Path]::IsPathRooted($OutputPath)) {
    $root = Split-Path -Parent $PSScriptRoot
    $OutputPath = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
}
$directory = Split-Path -Parent $OutputPath
New-Item -ItemType Directory -Force -Path $directory | Out-Null
$comparison | ConvertTo-Json -Depth 8 | Set-Content -Encoding utf8 $OutputPath
$comparison.metrics | ForEach-Object {
    [PSCustomObject]@{
        Metric = $_.name
        P50ChangePercent = [Math]::Round([double]$_.p50Milliseconds.changePercent, 2)
        P95ChangePercent = [Math]::Round([double]$_.p95Milliseconds.changePercent, 2)
        P99ChangePercent = [Math]::Round([double]$_.p99Milliseconds.changePercent, 2)
        BaselineFailures = $_.failures.baseline
        CurrentFailures = $_.failures.current
    }
} | Format-Table -AutoSize
Write-Host "Comparison written to $OutputPath"
