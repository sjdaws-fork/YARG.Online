# Create-BastionSession.ps1
#
# Creates (or reuses) an OCI Bastion PORT_FORWARDING session against the given
# bastion and brings up a local ssh -L tunnel in the foreground. Press Ctrl+C
# (or close the console) to tear the tunnel down. The bastion session itself is
# left ACTIVE so the next invocation can reuse it instead of paying the
# ~30-60s session-provision cost again.
#
# Reuse is identified by display-name convention:
#   auto-<sshKeyFingerprint>-<targetIp>
# Sessions matching that displayName (created by this script for this private
# key and target) are reused; anything else is left alone.
#
# Requires the OCI CLI (`oci`) on PATH and Windows OpenSSH (`ssh`, `ssh-keygen`).

[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string]$BastionId,
    [Parameter(Mandatory)] [string]$TargetIp,
    [Parameter(Mandatory)] [string]$PublicKeyPath,
    [Parameter(Mandatory)] [string]$PrivateKeyPath,
    [int]$TargetPort = 6443,
    [int]$LocalPort = 6443,
    [int]$TtlSeconds = 10800,
    [int]$MinRemainingSeconds = 600
)

$ErrorActionPreference = 'Stop'

if (-not (Test-Path $PublicKeyPath))  { throw "PublicKeyPath not found: $PublicKeyPath" }
if (-not (Test-Path $PrivateKeyPath)) { throw "PrivateKeyPath not found: $PrivateKeyPath" }

# Refuse to clobber an existing listener — the ssh -L would fail with EADDRINUSE
# anyway, but failing loudly here gives a clearer error than ssh's bind message.
$inUse = Get-NetTCPConnection -LocalAddress '127.0.0.1' -LocalPort $LocalPort -State Listen -ErrorAction SilentlyContinue
if ($inUse) {
    throw "Port $LocalPort is already in use on 127.0.0.1 — close the existing tunnel first."
}

$sshProc = $null
try {
    # SHA256 fingerprint, sanitized to alphanumeric for use inside a displayName.
    $fpRaw = & ssh-keygen -lf $PublicKeyPath 2>&1
    if ($LASTEXITCODE -ne 0) {
        throw "ssh-keygen -lf failed (exit $LASTEXITCODE): $fpRaw"
    }
    $fp        = ($fpRaw -split '\s+')[1]            # "SHA256:<base64>"
    $fpClean   = ($fp -replace '[^A-Za-z0-9]', '')
    if ($fpClean.Length -gt 24) { $fpClean = $fpClean.Substring(0, 24) }
    $ipClean   = $TargetIp -replace '\.', '-'
    $expectedName = "auto-$fpClean-$ipClean"

    Write-Host "Looking for reusable session ($expectedName)…"
    $listJson = oci bastion session list `
        --bastion-id $BastionId `
        --session-lifecycle-state ACTIVE `
        --all `
        --output json 2>$null

    $sessions = @()
    if ($LASTEXITCODE -eq 0 -and $listJson) {
        $parsed = $listJson | ConvertFrom-Json
        if ($parsed.data) { $sessions = @($parsed.data) }
    }

    # OCI CLI emits JSON in kebab-case (despite the underlying REST API using
    # camelCase). Property access uses quoted-name syntax to match.
    $now = [DateTimeOffset]::UtcNow
    function Get-RemainingSeconds($sess) {
        $created = [DateTimeOffset]::Parse($sess.'time-created')
        $ttl     = [int]$sess.'session-ttl-in-seconds'
        [int]($created.AddSeconds($ttl) - $now).TotalSeconds
    }

    $reusable = $sessions | Where-Object {
        $_.'display-name' -eq $expectedName -and
        $_.'target-resource-details' -and
        $_.'target-resource-details'.'session-type' -eq 'PORT_FORWARDING' -and
        $_.'target-resource-details'.'target-resource-private-ip-address' -eq $TargetIp -and
        [int]$_.'target-resource-details'.'target-resource-port' -eq $TargetPort -and
        (Get-RemainingSeconds $_) -gt $MinRemainingSeconds
    } | Sort-Object -Property @{ Expression = { Get-RemainingSeconds $_ }; Descending = $true }

    $session = $reusable | Select-Object -First 1

    if ($session) {
        $sessionId  = $session.id
        $reuseLabel = 'reused'
    }
    else {
        Write-Host "No reusable session; creating new one (~30-60s)…"
        $createJson = oci bastion session create-port-forwarding `
            --bastion-id $BastionId `
            --display-name $expectedName `
            --key-type PUB `
            --ssh-public-key-file $PublicKeyPath `
            --target-private-ip $TargetIp `
            --target-port $TargetPort `
            --session-ttl $TtlSeconds `
            --wait-for-state SUCCEEDED `
            --output json
        if ($LASTEXITCODE -ne 0 -or -not $createJson) {
            throw "oci bastion session create-port-forwarding failed (exit $LASTEXITCODE)."
        }
        # With --wait-for-state, the CLI returns the work request, not the session.
        # The created session's OCID is in data.resources[].identifier — but the
        # entityType string varies by service version, so match on the OCID prefix
        # (every bastion session OCID starts with "ocid1.bastionsession.").
        $wr = ($createJson | ConvertFrom-Json).data
        $sessionResource = $wr.resources |
            Where-Object { $_.identifier -like 'ocid1.bastionsession.*' } |
            Select-Object -First 1
        if (-not $sessionResource) {
            throw "create-port-forwarding work request did not report a created session. Full response:`n$createJson"
        }
        $sessionId  = $sessionResource.identifier
        $reuseLabel = 'created'
    }

    # Fetch full session details. SessionSummary (from list) and the create
    # work request both omit sshMetadata; only `session get` returns it, and
    # we need it to know the correct bastion hostname for this region.
    $getJson = oci bastion session get --session-id $sessionId --output json 2>$null
    if ($LASTEXITCODE -ne 0 -or -not $getJson) {
        throw "oci bastion session get failed for $sessionId (exit $LASTEXITCODE)."
    }
    $full   = ($getJson | ConvertFrom-Json).data
    $sshCmd = $full.'ssh-metadata'.command
    if (-not $sshCmd -or $sshCmd -notmatch '@([A-Za-z0-9.\-]+oci\.oraclecloud\.com)') {
        throw "Could not parse bastion host from session $sessionId. Full response:`n$getJson"
    }
    $bastionHost = $Matches[1]

    $remaining    = Get-RemainingSeconds $full
    $remainingTxt = '{0}h {1}m' -f [int]($remaining/3600), [int](($remaining%3600)/60)
    Write-Host "Session: $sessionId ($reuseLabel, $remainingTxt remaining)"
    Write-Host "Tunnel:  127.0.0.1:$LocalPort -> ${TargetIp}:$TargetPort"
    Write-Host "Ctrl+C to close."

    $sshArgs = @(
        '-i', $PrivateKeyPath,
        '-N',
        '-L', ('{0}:{1}:{2}' -f $LocalPort, $TargetIp, $TargetPort),
        '-o', 'StrictHostKeyChecking=accept-new',
        '-o', 'ServerAliveInterval=30',
        '-p', '22',
        ('{0}@{1}' -f $sessionId, $bastionHost)
    )

    # -NoNewWindow shares the console so Ctrl+C reaches ssh too. The finally
    # is a safety net for cases where ssh outlives an exceptional script exit.
    $sshProc = Start-Process -FilePath ssh -ArgumentList $sshArgs -NoNewWindow -PassThru
    Wait-Process -Id $sshProc.Id
}
finally {
    if ($sshProc -and -not $sshProc.HasExited) {
        Stop-Process -Id $sshProc.Id -Force -ErrorAction SilentlyContinue
    }
}

if ($sshProc -and $sshProc.HasExited) { exit $sshProc.ExitCode }
