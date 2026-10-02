$script:TabLinkDeviceSerialBindingAlgorithm = 'sha256-utf8-tablink-device-serial-v1'
$script:TabLinkDeviceSerialBindingDomain = "tablink-device-serial-v1`0"
$script:TabLinkTrustedAdbRuntime = [ordered]@{
    'adb.exe' = '957E46B8615F7AF5B7292A2DDABE98D2E61940C3FB2B0545756507F080613E71'
    'AdbWinApi.dll' = '120BEF587119C6CB926B86B9BE90FDFBCE38937588EAE28CD91A94CE63C7B965'
    'AdbWinUsbApi.dll' = '6CA69A2CA0E31309C087D288F058977D421AD03500E4C3E1DBD981241A069C60'
}

function Resolve-TabLinkTrustedAdbPath {
    [CmdletBinding()]
    param(
        [AllowEmptyString()][string]$Adb,
        [Parameter(Mandatory = $true)][string]$ProjectRoot
    )

    $candidate = if ([string]::IsNullOrWhiteSpace($Adb)) {
        Join-Path $ProjectRoot 'third_party\adb\bin\adb.exe'
    }
    else {
        # Windows PowerShell 5.1 and PowerShell 7 disagree about
        # IsPathFullyQualified for drive-relative paths. Accept only an
        # explicit drive-rooted or UNC path on both runtimes.
        if ($Adb -cnotmatch '\A(?:[A-Za-z]:[\\/]|\\\\[^\\/]+[\\/][^\\/]+(?:[\\/]|$))') {
            throw 'ADB must be an explicit absolute path; PATH lookup is not trusted.'
        }
        [IO.Path]::GetFullPath($Adb)
    }
    if (-not [string]::Equals([IO.Path]::GetFileName($candidate), 'adb.exe', [StringComparison]::OrdinalIgnoreCase)) {
        throw 'ADB path must identify adb.exe.'
    }
    $runtimeDirectory = Split-Path -Parent $candidate
    foreach ($entry in $script:TabLinkTrustedAdbRuntime.GetEnumerator()) {
        $runtimePath = Join-Path $runtimeDirectory $entry.Key
        if (-not (Test-Path -LiteralPath $runtimePath -PathType Leaf)) {
            throw "Trusted ADB runtime member is missing: $($entry.Key)"
        }
        $actual = (Get-FileHash -LiteralPath $runtimePath -Algorithm SHA256).Hash
        if (-not [string]::Equals($actual, $entry.Value, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Trusted ADB runtime hash mismatch: $($entry.Key)"
        }
    }
    return [IO.Path]::GetFullPath($candidate)
}

function Get-TabLinkDeviceSerialSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)][string]$Serial)

    # v1 canonical form: exact case-sensitive USB serial, without trimming or
    # Unicode normalization, after the ASCII-only validation below. Hash the
    # UTF-8 bytes of "tablink-device-serial-v1\0" + serial and emit lowercase hex.
    if ($Serial.Length -gt 256 -or $Serial.StartsWith('-', [StringComparison]::Ordinal) -or
        $Serial -notmatch '\A[A-Za-z0-9._+\-]+\z' -or
        $Serial.StartsWith('emulator-', [StringComparison]::OrdinalIgnoreCase) -or
        $Serial.IndexOf('._tcp', [StringComparison]::OrdinalIgnoreCase) -ge 0 -or
        $Serial.IndexOf('_adb-tls', [StringComparison]::OrdinalIgnoreCase) -ge 0) {
        throw 'Invalid physical USB serial.'
    }
    $bytes = [Text.Encoding]::UTF8.GetBytes($script:TabLinkDeviceSerialBindingDomain + $Serial)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha256.ComputeHash($bytes) }
    finally { $sha256.Dispose() }
    return ([BitConverter]::ToString($hash)).Replace('-', '').ToLowerInvariant()
}

function ConvertTo-TabLinkFiniteRate {
    [CmdletBinding()]
    param([Parameter(Mandatory = $true)]$Value)

    $rate = [double]$Value
    if ([double]::IsNaN($rate) -or [double]::IsInfinity($rate) -or $rate -lt 0 -or $rate -gt 1000) {
        throw 'Host health contains an invalid frame rate.'
    }
    return $rate
}

function ConvertTo-TabLinkBoundHostTelemetry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]$CandidateHealth,
        [Parameter(Mandatory = $true)][string]$ExpectedDeviceSerialSha256,
        [Parameter(Mandatory = $true)][DateTimeOffset]$Now,
        [Parameter(Mandatory = $true)]$OwnerProcess
    )

    try {
        $requiredNames = @(
            'timestamp', 'pid', 'transport', 'deviceSerialSha256',
            'measuredPresentedFps', 'ClientSubmittedFps', 'ClientPresentedFps'
        )
        $actualNames = @($CandidateHealth.PSObject.Properties.Name)
        foreach ($name in $requiredNames) {
            if ($actualNames -cnotcontains $name) { return $null }
        }

        if ($ExpectedDeviceSerialSha256 -notmatch '\A[0-9a-f]{64}\z' -or
            [string]$CandidateHealth.deviceSerialSha256 -notmatch '\A[0-9a-f]{64}\z' -or
            -not [string]::Equals(
                [string]$CandidateHealth.deviceSerialSha256,
                $ExpectedDeviceSerialSha256,
                [StringComparison]::Ordinal)) { return $null }
        if (-not [string]::Equals([string]$CandidateHealth.transport, 'ADB', [StringComparison]::Ordinal)) {
            return $null
        }

        $timestamp = [DateTimeOffset]$CandidateHealth.timestamp
        $ownerPid = [int]$CandidateHealth.pid
        $ownerStarted = [DateTimeOffset]$OwnerProcess.StartTime
        $age = $Now - $timestamp
        if ($ownerPid -le 0 -or [int]$OwnerProcess.Id -ne $ownerPid -or
            -not [string]::Equals([string]$OwnerProcess.ProcessName, 'TabLink', [StringComparison]::OrdinalIgnoreCase) -or
            $age.TotalSeconds -lt 0 -or $age.TotalSeconds -gt 10 -or $timestamp -lt $ownerStarted) {
            return $null
        }

        return [pscustomobject]@{
            timestamp = $timestamp.ToString('o')
            pid = $ownerPid
            receiving = if ($actualNames -ccontains 'receiving') { [bool]$CandidateHealth.receiving } else { $null }
            hostPresentationCallbackDeltaFps = ConvertTo-TabLinkFiniteRate $CandidateHealth.measuredPresentedFps
            clientSubmittedFps = ConvertTo-TabLinkFiniteRate $CandidateHealth.ClientSubmittedFps
            clientPresentationCallbackFps = ConvertTo-TabLinkFiniteRate $CandidateHealth.ClientPresentedFps
            framesSent = if ($actualNames -ccontains 'FramesSent') { [long]$CandidateHealth.FramesSent } else { $null }
            presentedFrames = if ($actualNames -ccontains 'PresentedFrames') { [long]$CandidateHealth.PresentedFrames } else { $null }
        }
    }
    catch {
        return $null
    }
}
