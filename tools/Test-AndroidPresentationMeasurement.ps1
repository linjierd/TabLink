param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$script:assertionCount = 0

function Assert-Contract([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "Android presentation measurement contract failed: $Message" }
    $script:assertionCount++
}

$repository = [IO.Path]::GetFullPath($RepositoryRoot)
$corePath = Join-Path $repository 'tools\Measure-AndroidPresentation.Core.ps1'
$measurementPath = Join-Path $repository 'tools\Measure-AndroidPresentation.ps1'
$mainFormPath = Join-Path $repository 'src\TabLink.Windows\MainForm.cs'
. $corePath

$syntheticSerial = 'TEST-TABLET-SERIAL-0001'
$expectedHash = 'd0798da604e052599b47f73139db7a378687c7383991af733d3ae65240010f9f'
$actualHash = Get-TabLinkDeviceSerialSha256 $syntheticSerial
Assert-Contract ($actualHash -ceq $expectedHash) 'the documented v1 serial binding vector changed'
Assert-Contract ((Get-TabLinkDeviceSerialSha256 'test-tablet-serial-0001') -cne $actualHash) 'serial canonicalization must remain case-sensitive'
Assert-Contract ((Get-TabLinkDeviceSerialSha256 'TEST+TABLET') -match '\A[0-9a-f]{64}\z') 'a valid USB serial character was rejected'
foreach ($invalidSerial in @('192.0.2.1:5555', 'emulator-5554', 'adb-test._adb-tls-connect._tcp', '-option')) {
    $rejected = $false
    try { [void](Get-TabLinkDeviceSerialSha256 $invalidSerial) } catch { $rejected = $true }
    Assert-Contract $rejected "non-USB serial was accepted: $invalidSerial"
}

$now = [DateTimeOffset]::Parse('2026-10-02T01:02:03+00:00')
$owner = [pscustomobject]@{ Id = 4242; ProcessName = 'TabLink'; StartTime = $now.AddMinutes(-1) }
$health = [pscustomobject][ordered]@{
    timestamp = $now.AddSeconds(-1).ToString('o')
    pid = 4242
    transport = 'ADB'
    deviceSerialSha256 = $expectedHash
    receiving = $true
    measuredPresentedFps = 61.25
    ClientSubmittedFps = 87.5
    ClientPresentedFps = 86.75
    FramesSent = 1234
    PresentedFrames = 1200
}
$telemetry = ConvertTo-TabLinkBoundHostTelemetry -CandidateHealth $health `
    -ExpectedDeviceSerialSha256 $actualHash -Now $now -OwnerProcess $owner
Assert-Contract ($null -ne $telemetry) 'a fresh hash-bound ADB health sample without a raw serial was rejected'
Assert-Contract ($telemetry.hostPresentationCallbackDeltaFps -eq 61.25) 'measuredPresentedFps mapping drifted'
Assert-Contract ($telemetry.clientSubmittedFps -eq 87.5) 'ClientSubmittedFps mapping drifted'
Assert-Contract ($telemetry.clientPresentationCallbackFps -eq 86.75) 'ClientPresentedFps mapping drifted'
$serializedTelemetry = $telemetry | ConvertTo-Json -Compress
Assert-Contract ($serializedTelemetry.IndexOf($syntheticSerial, [StringComparison]::Ordinal) -lt 0) 'resolved telemetry disclosed a raw serial'

$wrongBinding = $health.PSObject.Copy()
$wrongBinding.deviceSerialSha256 = '0' * 64
Assert-Contract ($null -eq (ConvertTo-TabLinkBoundHostTelemetry $wrongBinding $actualHash $now $owner)) 'a different device binding was accepted'
$stale = $health.PSObject.Copy()
$stale.timestamp = $now.AddSeconds(-11).ToString('o')
Assert-Contract ($null -eq (ConvertTo-TabLinkBoundHostTelemetry $stale $actualHash $now $owner)) 'a stale host sample was accepted'
$wrongTransport = $health.PSObject.Copy()
$wrongTransport.transport = 'TLS'
Assert-Contract ($null -eq (ConvertTo-TabLinkBoundHostTelemetry $wrongTransport $actualHash $now $owner)) 'a non-ADB session was bound to an ADB measurement'

$measurement = Get-Content -LiteralPath $measurementPath -Raw
$core = Get-Content -LiteralPath $corePath -Raw
$mainForm = Get-Content -LiteralPath $mainFormPath -Raw
Assert-Contract ($measurement -match 'SurfaceFlinger --latency') 'SurfaceFlinger actual-present timestamps are no longer the primary measurement'
Assert-Contract ($measurement -match "SurfaceFlinger --list\)\s*`r?`nif \(\`$LASTEXITCODE -ne 0\)") 'SurfaceFlinger layer enumeration does not check the native exit code'
Assert-Contract ($measurement -match 'physicalPresentationFps=\$wallWindowFps') 'physical presentation FPS is not explicitly emitted'
Assert-Contract ($measurement -match 'hostHealthMatched=\(\$null -ne \$finalHostTelemetry\)') 'final host-health match state is not emitted'
Assert-Contract ($measurement -match '\$finalHostTelemetry = \$hostTelemetry') 'top-level telemetry is not replaced on every final sample'
Assert-Contract ($measurement -notmatch 'latestHostTelemetry') 'top-level telemetry can still retain an earlier stale health sample'
Assert-Contract ($measurement -match 'hostPresentationCallbackDeltaFps=\$finalHostTelemetry\.hostPresentationCallbackDeltaFps') 'host callback telemetry is not mapped explicitly'
Assert-Contract ($measurement -match 'clientSubmittedFps=\$finalHostTelemetry\.clientSubmittedFps') 'client submission telemetry is not mapped explicitly'
Assert-Contract ($measurement -match 'clientPresentationCallbackFps=\$finalHostTelemetry\.clientPresentationCallbackFps') 'client presentation callback telemetry is not mapped explicitly'
Assert-Contract ($measurement -notmatch 'deviceSerial\s*=\s*\$Serial') 'measurement JSON still emits a raw serial'
Assert-Contract ($measurement -notmatch '\$candidateHealth\.serial|ClientReportedFps|\$health\.measuredFps') 'measurement still relies on a removed or ambiguous health field'
Assert-Contract ($measurement -match 'Resolve-TabLinkTrustedAdbPath -Adb \$Adb') 'measurement does not resolve a fixed-hash ADB runtime'
Assert-Contract ($measurement -notmatch "\[string\]\`$Adb\s*=\s*'adb'") 'measurement still permits an implicit PATH ADB default'
foreach ($trustedHash in @(
    '957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71',
    '120BEF587119C6CB926B86B9BE90FDFBCE38937588EAE28CD91A94CE63C7B965',
    '6CA69A2CA0E31309C087D288F058977D421AD03500E4C3E1DBD981241A069C60'
)) {
    Assert-Contract ($core.Contains($trustedHash, [StringComparison]::Ordinal)) "trusted ADB runtime hash missing: $trustedHash"
}
foreach ($unqualifiedAdbPath in @(
    'adb.exe',
    '.\platform-tools\adb.exe',
    'C:platform-tools\adb.exe',
    '\platform-tools\adb.exe'
)) {
    try {
        Resolve-TabLinkTrustedAdbPath -Adb $unqualifiedAdbPath -ProjectRoot $repository
        throw "unqualified ADB path was accepted: $unqualifiedAdbPath"
    }
    catch {
        Assert-Contract ($_.Exception.Message -match 'explicit absolute path') "unqualified ADB path did not fail closed: $unqualifiedAdbPath"
    }
}
Assert-Contract ($mainForm -match 'deviceSerialSha256=observedApproval is null\?null:DeviceSerialBinding\.ComputeSha256\(observedApproval\.Serial\)') 'host health does not emit the matching privacy-preserving binding'

Write-Output "Android presentation measurement contract: $script:assertionCount assertions passed."
