param(
    [Parameter(Mandatory=$true)][string]$Serial,
    [Parameter(Mandatory=$true)][string]$OutputPath,
    [int]$Seconds = 30,
    [string]$Adb,
    [string]$HostHealthPath
)
$ErrorActionPreference = 'Stop'
if ($Seconds -lt 10 -or $Seconds -gt 120) { throw 'Use a bounded 10 to 120 second measurement.' }
. (Join-Path $PSScriptRoot 'Measure-AndroidPresentation.Core.ps1')
$projectRoot = Split-Path -Parent $PSScriptRoot
$Adb = Resolve-TabLinkTrustedAdbPath -Adb $Adb -ProjectRoot $projectRoot
$deviceSerialSha256 = Get-TabLinkDeviceSerialSha256 $Serial
$healthPath = if ($HostHealthPath) { [IO.Path]::GetFullPath($HostHealthPath) } else { Join-Path ([Environment]::GetFolderPath('LocalApplicationData')) 'TabLink\diagnostics\session-health.json' }
$layerOutput = @(& $Adb -s $Serial shell dumpsys SurfaceFlinger --list)
if ($LASTEXITCODE -ne 0) { throw 'SurfaceFlinger layer enumeration failed.' }
$layers = @($layerOutput | Where-Object { $_ -match '^SurfaceView\[com\.tablink\.client/com\.tablink\.client\.MainActivity\]\(BLAST\)#\d+$' })
if ($layers.Count -ne 1) { throw 'Expected exactly one active TabLink video SurfaceView.' }
$layer = $layers[0].Trim()
$actualTimes = [System.Collections.Generic.SortedSet[long]]::new()
$samples = [System.Collections.Generic.List[object]]::new()
$raw = [System.Collections.Generic.List[string]]::new()
$coverageGaps = 0
$previousNewest = 0L
$firstBaseline = 0L
$periodNs = 0L
$windowStartSeconds = $null
$windowSeconds = 0.0
$firstObservedAdvanceSeconds = $null
$lastObservedAdvanceSeconds = 0.0
$maxObservedNoAdvanceSeconds = 0.0
$finalHostTelemetry = $null
$watch = [Diagnostics.Stopwatch]::StartNew()
do {
    $stamp = [DateTimeOffset]::Now.ToString('o')
    $lines = @(& $Adb -s $Serial shell dumpsys SurfaceFlinger --latency ('"' + $layer + '"'))
    $sampleCompletedSeconds = $watch.Elapsed.TotalSeconds
    if ($LASTEXITCODE -ne 0) { throw 'SurfaceFlinger measurement failed.' }
    $raw.Add("Sample $stamp")
    foreach ($line in $lines) { $raw.Add($line) }
    if ($lines.Count -lt 2 -or $lines[0] -notmatch '^\d+$') { throw 'Video layer disappeared during measurement.' }
    $currentPeriod = [long]$lines[0]
    if ($currentPeriod -le 0) { throw 'Physical panel period is not positive.' }
    if ($periodNs -ne 0 -and $periodNs -ne $currentPeriod) { throw 'Physical panel period changed during measurement.' }
    $periodNs = $currentPeriod
    $currentTimes = @($lines | Select-Object -Skip 1 | ForEach-Object {
        if ($_ -match '^\s*(\d+)\s+(\d+)\s+(\d+)\s*$') {
            $actual = [long]$Matches[2]
            if ($actual -gt 0 -and $actual -lt [long]::MaxValue) { $actual }
        }
    } | Sort-Object -Unique)
    if ($currentTimes.Count -lt 2) { throw 'Insufficient actual presentation timestamps.' }
    if ($firstBaseline -eq 0) {
        $firstBaseline = $currentTimes[-1]
        $windowStartSeconds = $sampleCompletedSeconds
    }
    $windowSeconds = $sampleCompletedSeconds - $windowStartSeconds
    if ($previousNewest -gt 0) {
        if ($currentTimes[-1] -lt $previousNewest) { throw 'Actual presentation timeline moved backwards.' }
        # Retaining the last observed frame proves adjacent ring samples overlap.
        if ($currentTimes -notcontains $previousNewest) { $coverageGaps++ }
        $maxObservedNoAdvanceSeconds = [Math]::Max($maxObservedNoAdvanceSeconds,
            $windowSeconds - $lastObservedAdvanceSeconds)
        if ($currentTimes[-1] -gt $previousNewest) {
            if ($null -eq $firstObservedAdvanceSeconds) { $firstObservedAdvanceSeconds = $windowSeconds }
            $lastObservedAdvanceSeconds = $windowSeconds
        }
    }
    $previousNewest = $currentTimes[-1]
    foreach ($actual in $currentTimes) { if ($actual -ge $firstBaseline) { [void]$actualTimes.Add($actual) } }
    $hostTelemetry = $null
    try {
        $candidateHealth = Get-Content -LiteralPath $healthPath -Raw | ConvertFrom-Json
        $healthOwner = Get-Process -Id ([int]$candidateHealth.pid) -ErrorAction Stop
        $hostTelemetry = ConvertTo-TabLinkBoundHostTelemetry -CandidateHealth $candidateHealth `
            -ExpectedDeviceSerialSha256 $deviceSerialSha256 -Now ([DateTimeOffset]::Now) `
            -OwnerProcess $healthOwner
    } catch { }
    # The top-level callback summary represents only the final sample. Never
    # retain an earlier value after health becomes stale, changes transport or
    # stops matching this device; the per-sample history remains below.
    $finalHostTelemetry = $hostTelemetry
    $samples.Add([pscustomobject]@{
        capturedAt=$stamp; completedAt=[DateTimeOffset]::Now.ToString('o');
        windowElapsedSeconds=$windowSeconds; newestActualPresentationNs=$previousNewest;
        observedNoAdvanceSeconds=$windowSeconds-$lastObservedAdvanceSeconds;
        hostHealthMatched=($null -ne $hostTelemetry);
        hostHealthTimestamp=$hostTelemetry.timestamp; hostProcessId=$hostTelemetry.pid;
        receiving=$hostTelemetry.receiving;
        hostPresentationCallbackDeltaFps=$hostTelemetry.hostPresentationCallbackDeltaFps;
        clientSubmittedFps=$hostTelemetry.clientSubmittedFps;
        clientPresentationCallbackFps=$hostTelemetry.clientPresentationCallbackFps;
        framesSent=$hostTelemetry.framesSent; presentedFrames=$hostTelemetry.presentedFrames
    })
    # Take the final snapshot after the full requested window, including a frozen tail.
    if ($windowSeconds -ge $Seconds) { break }
    Start-Sleep -Milliseconds 500
} while ($true)
$ordered = @($actualTimes)
$intervals = @(for ($i=1; $i -lt $ordered.Count; $i++) { ($ordered[$i]-$ordered[$i-1])/1e6 })
$sortedIntervals = @($intervals | Sort-Object)
$duration = ($ordered[-1]-$ordered[0])/1e9
$newPresentationCount = $ordered.Count - 1
$wallWindowFps = $newPresentationCount / $windowSeconds
$actualTimelineFps = if ($duration -gt 0) { $newPresentationCount / $duration } else { $null }
$medianMs = $p95Ms = $p99Ms = $maxMs = $null
if ($sortedIntervals.Count -gt 0) {
    $medianMs = $sortedIntervals[[int][Math]::Floor(($sortedIntervals.Count-1)*0.5)]
    $p95Ms = $sortedIntervals[[int][Math]::Floor(($sortedIntervals.Count-1)*0.95)]
    $p99Ms = $sortedIntervals[[int][Math]::Floor(($sortedIntervals.Count-1)*0.99)]
    $maxMs = $sortedIntervals[-1]
}
$missedSlots = 0
foreach ($interval in $intervals) { $missedSlots += [Math]::Max(0, [Math]::Round($interval*1e6/$periodNs)-1) }
$result = [pscustomobject]@{
    capturedAt=[DateTimeOffset]::Now.ToString('o');
    deviceBindingAlgorithm=$script:TabLinkDeviceSerialBindingAlgorithm;
    deviceSerialSha256=$deviceSerialSha256; videoLayer=$layer;
    displayPeriodNs=$periodNs; physicalRefreshRateHz=1e9/$periodNs;
    actualPresentationCount=$ordered.Count; newPresentationCount=$newPresentationCount;
    durationSeconds=$duration; actualTimelineSeconds=$duration;
    requestedWindowSeconds=$Seconds; wallWindowSeconds=$windowSeconds;
    physicalPresentationFps=$wallWindowFps;
    actualPresentedFps=$wallWindowFps; wallWindowFps=$wallWindowFps; actualTimelineFps=$actualTimelineFps;
    hostHealthMatched=($null -ne $finalHostTelemetry);
    hostHealthTimestamp=$finalHostTelemetry.timestamp;
    hostPresentationCallbackDeltaFps=$finalHostTelemetry.hostPresentationCallbackDeltaFps;
    clientSubmittedFps=$finalHostTelemetry.clientSubmittedFps;
    clientPresentationCallbackFps=$finalHostTelemetry.clientPresentationCallbackFps;
    firstObservedAdvanceSeconds=$firstObservedAdvanceSeconds;
    trailingNoObservedAdvanceSeconds=$windowSeconds-$lastObservedAdvanceSeconds;
    maxTimeBetweenObservedAdvancesSeconds=$maxObservedNoAdvanceSeconds;
    presentIntervalMedianMs=$medianMs; presentIntervalP95Ms=$p95Ms; presentIntervalP99Ms=$p99Ms;
    presentIntervalMaxMs=$maxMs; missedVsyncSlots=$missedSlots;
    continuousCoverage=($coverageGaps -eq 0); pollCoverageGaps=$coverageGaps; samples=$samples;
    metricSemantics=[pscustomobject]@{
        physicalPresentationFps='SurfaceFlinger second-column actual-present timestamps over the host Stopwatch window.';
        hostPresentationCallbackDeltaFps='Host PresentedFrames callback-count delta from session-health.measuredPresentedFps; callback telemetry only, never physical presentation FPS.';
        clientSubmittedFps='Android decoder submission telemetry from session-health.ClientSubmittedFps; not presentation FPS.';
        clientPresentationCallbackFps='Android presentation callback telemetry from session-health.ClientPresentedFps; callback telemetry only, never physical presentation FPS.'
    };
    method='Read-only SurfaceFlinger --latency; union of valid unique second-column actual-present timestamps. First snapshot supplies a baseline only. physicalPresentationFps=actualPresentedFps=wallWindowFps=new presentations/(host Stopwatch elapsed between first and final snapshot completion), including frozen head/tail; the final snapshot is at least the requested duration later. actualTimelineFps=(count-1)/(last-first) describes only the Android presentation span and excludes a frozen tail. Host and client callback rates are separately named telemetry and are never substituted for physicalPresentationFps. Host polling adds boundary uncertainty; observed advance delays have polling granularity and do not compare host/device clocks. Coverage gaps invalidate whole-window rate. Interval/missed-slot statistics cover only the Android presentation span. Buffer presentation does not alone prove unique picture content.'
}
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding UTF8
$raw | Set-Content -LiteralPath ([IO.Path]::ChangeExtension($OutputPath,'.latency.txt')) -Encoding UTF8
$result | Select-Object capturedAt,actualPresentationCount,newPresentationCount,wallWindowSeconds,physicalPresentationFps,actualTimelineFps,hostPresentationCallbackDeltaFps,clientSubmittedFps,clientPresentationCallbackFps,trailingNoObservedAdvanceSeconds,presentIntervalP95Ms,presentIntervalP99Ms,presentIntervalMaxMs,missedVsyncSlots,pollCoverageGaps | ConvertTo-Json
if ($coverageGaps -gt 0) { throw 'Measurement has uncovered ring-buffer gaps; do not treat it as continuous.' }
