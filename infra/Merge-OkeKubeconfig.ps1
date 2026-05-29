# Merge-OkeKubeconfig.ps1
#
# Merges the OKE kubeconfig from Pulumi stack output into the user's default
# kubeconfig ($HOME\.kube\config) as a renamed context, so `kubectl config
# use-context` can switch into it without juggling $env:KUBECONFIG.
#
# The merged context still relies on the bastion tunnel — its server is
# `https://127.0.0.1:6443`, so `Open-BastionTunnel.ps1` must be run before any
# kubectl/skaffold call against it.
#
# Re-running this script after `pulumi up` is safe: --flatten overwrites the
# existing context/cluster/user entries in place rather than duplicating, as
# long as -ContextName stays the same.

[CmdletBinding()]
param(
    [string]$Stack = 'oci-prod',
    [string]$ContextName = 'yarg-oci-prod',
    [string]$KubeconfigPath = (Join-Path $HOME '.kube\config')
)

$ErrorActionPreference = 'Stop'
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $scriptDir

$kubeDir = Split-Path -Parent $KubeconfigPath
if (-not (Test-Path $kubeDir)) {
    New-Item -ItemType Directory -Path $kubeDir | Out-Null
}

$tmp = New-TemporaryFile
try {
    pulumi stack output --stack $Stack kubeConfig --show-secrets | Set-Content $tmp
    if ((Get-Item $tmp).Length -eq 0) {
        Write-Error "Pulumi returned an empty kubeConfig for stack '$Stack' — run `"pulumi up --stack $Stack`" first."
        exit 1
    }

    # The OKE generator emits a context like `context-<cluster-ocid-suffix>`.
    # Capture its name from the incoming file so we can rename it after merge.
    $incomingContext = (& kubectl --kubeconfig $tmp config current-context).Trim()
    if (-not $incomingContext) {
        Write-Error "Could not read current-context from the Pulumi kubeconfig."
        exit 1
    }

    # Back up the existing kubeconfig once per run — cheap insurance against a
    # bad merge clobbering unrelated contexts.
    if (Test-Path $KubeconfigPath) {
        $backup = "$KubeconfigPath.bak"
        Copy-Item $KubeconfigPath $backup -Force
        Write-Host "Backed up existing kubeconfig to $backup"
    }

    # KUBECONFIG accepts a `;`-separated list on Windows; --flatten inlines
    # certs/tokens so the result is self-contained, and earlier files win on
    # conflicts (so the existing config's unrelated entries are preserved).
    $env:KUBECONFIG = "$KubeconfigPath;$tmp"
    $merged = "$KubeconfigPath.merged"
    try {
        & kubectl config view --flatten --raw | Set-Content $merged
        if ($LASTEXITCODE -ne 0) {
            Write-Error "kubectl config view --flatten failed."
            exit 1
        }
        Move-Item $merged $KubeconfigPath -Force
    } finally {
        Remove-Item Env:KUBECONFIG -ErrorAction SilentlyContinue
        Remove-Item $merged -ErrorAction SilentlyContinue
    }

    # Rename the freshly-merged context if it isn't already our chosen name.
    # kubectl config rename-context errors if the target already exists, so
    # only rename when the incoming name differs from the desired one.
    if ($incomingContext -ne $ContextName) {
        # If a prior run already created $ContextName, drop it so the rename
        # lands cleanly. The cluster/user entries it referenced are about to
        # be replaced by the incoming ones anyway.
        $existing = (& kubectl --kubeconfig $KubeconfigPath config get-contexts -o name) -split "`n"
        if ($existing -contains $ContextName) {
            & kubectl --kubeconfig $KubeconfigPath config delete-context $ContextName | Out-Null
        }
        & kubectl --kubeconfig $KubeconfigPath config rename-context $incomingContext $ContextName | Out-Null
    }

    Write-Host "Merged OKE kubeconfig into $KubeconfigPath as context '$ContextName'."
    Write-Host "Switch with: kubectl config use-context $ContextName"
    Write-Host "Remember to run .\Open-BastionTunnel.ps1 before kubectl/skaffold calls."
} finally {
    Remove-Item $tmp -ErrorAction SilentlyContinue
}
