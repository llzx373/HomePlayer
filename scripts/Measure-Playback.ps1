param(
    [string]$MediaPath,
    [string]$OutputPath = "artifacts/performance/playback-current.json",
    [ValidateRange(1, 10000)][int]$OpenIterations = 30,
    [ValidateRange(1, 10000)][int]$SeekIterations = 100,
    [ValidateRange(1000, 3600000)][int]$SteadyMilliseconds = 5000,
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not $MediaPath) {
        $mediaDirectory = Join-Path $root "artifacts/performance/media"
        New-Item -ItemType Directory -Force -Path $mediaDirectory | Out-Null
        $MediaPath = Join-Path $mediaDirectory "baseline-1080p30-h264-aac.mp4"
        if (-not (Test-Path $MediaPath)) {
            & ffmpeg -hide_banner -loglevel error -y `
                -f lavfi -i "testsrc2=size=1920x1080:rate=30:duration=12" `
                -f lavfi -i "sine=frequency=440:sample_rate=48000:duration=12" `
                -c:v libx264 -preset veryfast -crf 20 -pix_fmt yuv420p `
                -c:a aac -b:a 128k -shortest $MediaPath
            if ($LASTEXITCODE -ne 0) { throw "FFmpeg fixture generation failed." }
        }
    }

    $MediaPath = (Resolve-Path $MediaPath).Path
    if (-not [IO.Path]::IsPathRooted($OutputPath)) {
        $OutputPath = [IO.Path]::GetFullPath((Join-Path $root $OutputPath))
    }
    $env:HOMEPLAYER_PERF_MEDIA = $MediaPath
    $env:HOMEPLAYER_PERF_OUTPUT = $OutputPath
    $env:HOMEPLAYER_PERF_OPENS = $OpenIterations.ToString()
    $env:HOMEPLAYER_PERF_SEEKS = $SeekIterations.ToString()
    $env:HOMEPLAYER_PERF_STEADY_MS = $SteadyMilliseconds.ToString()
    $build = @()
    if ($SkipBuild) { $build += "--no-build" }
    & dotnet test "tests/HomePlayer.Playback.Tests/HomePlayer.Playback.Tests.csproj" `
        -c Release --filter "FullyQualifiedName~PlaybackPerformanceTests" @build
    if ($LASTEXITCODE -ne 0) { throw "Playback performance measurement failed." }
    Write-Host "Performance result: $OutputPath"
}
finally {
    Remove-Item Env:HOMEPLAYER_PERF_MEDIA -ErrorAction SilentlyContinue
    Remove-Item Env:HOMEPLAYER_PERF_OUTPUT -ErrorAction SilentlyContinue
    Remove-Item Env:HOMEPLAYER_PERF_OPENS -ErrorAction SilentlyContinue
    Remove-Item Env:HOMEPLAYER_PERF_SEEKS -ErrorAction SilentlyContinue
    Remove-Item Env:HOMEPLAYER_PERF_STEADY_MS -ErrorAction SilentlyContinue
    Pop-Location
}
