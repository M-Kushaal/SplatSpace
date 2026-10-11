<#
.SYNOPSIS
  Run the backend on this PC so the phone app can upload captures over Wi-Fi.

.DESCRIPTION
  Finds this PC's LAN address, starts the API on 0.0.0.0:<Port> with signed URLs pointing at that
  address, and (with -Worker) starts a job worker in a second window.
  This PC has no GPU, so the worker runs real pose refinement but a dry-run (fake) training step:
  that exercises the whole flow end to end. Real training happens on a cloud GPU (build step 13).

  The first time, Windows Firewall asks whether Python may accept connections:
  allow it on PRIVATE networks only.

.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File backend\scripts\run_dev.ps1 -Worker
#>
param(
    [int]$Port = 8000,
    [switch]$Worker
)

$ErrorActionPreference = "Stop"
$backend = Split-Path -Parent $PSScriptRoot
$repo = Split-Path -Parent $backend

$ip = Get-NetIPAddress -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike "127.*" -and $_.IPAddress -notlike "169.254.*" -and $_.PrefixOrigin -ne "WellKnown" } |
    Sort-Object @{ Expression = { if ($_.InterfaceAlias -match "Wi-?Fi|WLAN") { 0 } elseif ($_.InterfaceAlias -match "Ethernet") { 1 } else { 2 } } } |
    Select-Object -First 1 -ExpandProperty IPAddress
if (-not $ip) { throw "Could not find this PC's LAN IP address. Are you connected to Wi-Fi?" }

$url = "http://${ip}:$Port"
$env:GS_PUBLIC_BASE_URL = $url

Write-Host ""
Write-Host "  GS Capture dev server" -ForegroundColor Cyan
Write-Host "  ---------------------"
Write-Host "  In the app: Settings -> Processing server -> " -NoNewline
Write-Host $url -ForegroundColor Green
Write-Host "  Phone and PC must be on the same Wi-Fi. Stop with Ctrl+C."
Write-Host ""

if ($Worker) {
    $workerCmd = "cd `"$repo\worker`"; python -m gs_worker --api http://127.0.0.1:$Port --dry-run --steps 50 --poll 10"
    # -NoProfile: a user profile that prompts for input would otherwise stall the worker window.
    Start-Process powershell -ArgumentList @("-NoProfile", "-NoExit", "-Command", $workerCmd) | Out-Null
    Write-Host "  Worker started in a new window (real pose refinement, dry-run training)." -ForegroundColor DarkGray
}

Set-Location $backend
python -m uvicorn app.main:app --host 0.0.0.0 --port $Port
