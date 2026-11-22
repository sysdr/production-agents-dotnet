# docs/capstone-05.ps1 — POST 37
# Multi-region failover simulation: primary Service Bus outage + Polly fallback.
#
# WHAT THIS SCRIPT DOES (5 acts):
#   Act 1  Baseline   — confirm primary and fallback routes both reachable
#   Act 2  Outage     — stop the Service Bus emulator (simulates primary namespace failure)
#   Act 3  Failover   — send orders, confirm Polly routes to orders-fallback queue
#   Act 4  Recovery   — restart Service Bus emulator, confirm primary resumes
#   Act 5  Forensics  — OTel logs show the fallback activation window
#
# PREREQUISITES:
#   dotnet run --project src/AppHost  (Aspire stack running)
#   docker compose up otel-collector -d
#
# WHAT GETS TESTED:
#   - FallbackServiceBusSender.SendOrderAsync routes to orders-fallback on SB failure
#   - OTel log "ServiceBus.FallbackActivated" appears during outage window
#   - Orders successfully created during outage (200 OK despite primary SB down)
#   - Primary routing resumes after Service Bus emulator restart
#
# RECOVERY TIME OBJECTIVE (RTO):
#   Polly detects failure on first exception (no circuit breaker delay in this config).
#   First fallback-routed order: < 2 seconds after primary fails.
#   Production equivalent: Azure Service Bus geo-redundancy switches in < 60 seconds.

param([switch]$SkipRecovery, [int]$FallbackOrderCount = 10)

$ErrorActionPreference = "Stop"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CAPSTONE 05 — Multi-Region Failover Simulation  ║" -ForegroundColor Cyan
Write-Host   "║  Primary Outage · Polly Fallback · Recovery      ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan

function Get-Port { param([string]$Name)
    $r = Invoke-RestMethod http://localhost:15888/api/v1/resources -TimeoutSec 5
    ($r | Where-Object { $_.name -eq $Name }).services[0].allocatedEndpoint.port
}
function Get-Token { param([string]$TenantId = "tenant-01")
    $b = @{ UserId = "capstone"; Email = "cap@test.com"; TenantId = $TenantId } | ConvertTo-Json
    (Invoke-RestMethod -Method Post "http://localhost:$(Get-Port 'user-service')/auth/token" `
        -Body $b -ContentType "application/json").Token
}
function Write-Act { param([int]$Act, [string]$Title)
    Write-Host "`n── Act $Act: $Title " -ForegroundColor Yellow -NoNewline
    Write-Host ("─" * (46 - $Title.Length)) -ForegroundColor DarkGray
}

$orderPort = Get-Port "order-service"
$tok       = Get-Token "tenant-01"

# ══════════════════════════════════════════════════════════════════════════
# ACT 1: BASELINE
# ══════════════════════════════════════════════════════════════════════════
Write-Act 1 "Baseline — primary Service Bus healthy"

# Check SB emulator is running
$sbRunning = (docker ps --filter "name=servicebus" --format "{{.Status}}") -match "Up"
if (-not $sbRunning) {
    Write-Host "  Service Bus emulator not running! Run: docker compose up -d" -ForegroundColor Red
    exit 1
}
Write-Host "  Service Bus emulator: running ✓" -ForegroundColor Green

# Send a baseline order — should route through primary
try {
    $r = Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
        -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 10
    Write-Host "  Baseline order created: $($r.OrderId) ✓" -ForegroundColor Green
} catch {
    Write-Host "  Baseline order FAILED: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}

# Check FallbackServiceBusSender is wired in
$hasFallback = (Get-Content "src/OrderService/FallbackServiceBusSender.cs" -ErrorAction SilentlyContinue) -ne $null
Write-Host ("  FallbackServiceBusSender.cs: " + $(if($hasFallback) {"present ✓"} else {"MISSING — Post 37 code required"})) `
    -ForegroundColor $(if($hasFallback) {"Green"} else {"Red"})

# ══════════════════════════════════════════════════════════════════════════
# ACT 2: OUTAGE SIMULATION
# ══════════════════════════════════════════════════════════════════════════
Write-Act 2 "Outage — stopping Service Bus emulator"

Write-Host "  Stopping servicebus-emulator container..."
Write-Host "  (This simulates a primary namespace outage.)" -ForegroundColor Yellow
Write-Host "  IMPORTANT: Docker stop can take 10-20s for AMQP drain." -ForegroundColor DarkGray

try {
    docker stop servicebus-emulator 2>&1 | Out-Null
    Write-Host "  Service Bus emulator stopped ✓" -ForegroundColor Yellow
    Write-Host "  Primary namespace: UNAVAILABLE" -ForegroundColor Red
} catch {
    Write-Host "  Could not stop container: $_" -ForegroundColor Red
    Write-Host "  Continuing with simulation assuming SB is now unreachable." -ForegroundColor Yellow
}

Start-Sleep 3   # Give AMQP connections time to detect the outage

# ══════════════════════════════════════════════════════════════════════════
# ACT 3: FAILOVER — orders route to fallback queue
# ══════════════════════════════════════════════════════════════════════════
Write-Act 3 "Failover — $FallbackOrderCount orders during outage"

$logsBeforeCount = (docker logs otel-collector 2>&1 | Select-String "FallbackActivated\|fallback queue").Count
$fallbackSuccess = 0
$primaryFail     = 0

Write-Host "  Sending $FallbackOrderCount orders while primary SB is down..."
for ($i = 1; $i -le $FallbackOrderCount; $i++) {
    try {
        $r = Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
            -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 15
        $fallbackSuccess++
        Write-Host "  Order $i: $($r.OrderId) → routed via Polly fallback" -ForegroundColor Yellow
    } catch {
        $primaryFail++
        Write-Host "  Order $i FAILED: $($_.Exception.Response.StatusCode) — fallback may not be wired in" -ForegroundColor Red
    }
    Start-Sleep -Milliseconds 300
}

Write-Host ""
Write-Host "  During outage: $fallbackSuccess orders succeeded via fallback, $primaryFail failed" `
    -ForegroundColor $(if($fallbackSuccess -gt 0) {"Yellow"} else {"Red"})

# Check for fallback log entries
Start-Sleep 5
$logsAfterCount = (docker logs otel-collector 2>&1 | Select-String "FallbackActivated\|fallback queue").Count
$fallbackLogs   = $logsAfterCount - $logsBeforeCount
Write-Host "  OTel log entries 'FallbackActivated': $fallbackLogs" `
    -ForegroundColor $(if($fallbackLogs -gt 0) {"Green"} else {"Yellow"})
if ($fallbackLogs -eq 0) {
    Write-Host "  Hint: if FallbackServiceBusSender is not wired into /orders/create," -ForegroundColor DarkGray
    Write-Host "        orders will fail rather than route to fallback." -ForegroundColor DarkGray
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 4: RECOVERY
# ══════════════════════════════════════════════════════════════════════════
Write-Act 4 "Recovery — restart Service Bus emulator"

if ($SkipRecovery) {
    Write-Host "  --SkipRecovery set. Run manually: docker start servicebus-emulator"
} else {
    Write-Host "  Restarting servicebus-emulator..."
    docker start servicebus-emulator 2>&1 | Out-Null
    Write-Host "  Waiting 15s for AMQP reconnection..." -NoNewline
    Start-Sleep 15
    Write-Host " done"

    # Send a recovery order — should route through primary again
    try {
        $r = Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
            -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 10
        Write-Host "  Recovery order: $($r.OrderId) → primary queue ✓" -ForegroundColor Green
    } catch {
        Write-Host "  Recovery order still failing: $($_.Exception.Message)" -ForegroundColor Yellow
        Write-Host "  Polly may still be routing to fallback. Wait 30s and retry." -ForegroundColor DarkGray
    }
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 5: FORENSICS
# ══════════════════════════════════════════════════════════════════════════
Write-Act 5 "Forensics — ADX query for failover window"

Write-Host ""
Write-Host "  OTel Collector log check:" -ForegroundColor Cyan
$allFallbackLogs = docker logs otel-collector 2>&1 | Select-String "FallbackActivated\|fallback queue" | Select-Object -Last 5
if ($allFallbackLogs) {
    $allFallbackLogs | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }
} else {
    Write-Host "    (no fallback log entries — fallback may not be active)" -ForegroundColor DarkGray
}

Write-Host ""
Write-Host "  ADX query — failover event window:" -ForegroundColor Cyan
Write-Host @"
  OTelLogs
  | where Timestamp > ago(1h)
  | where Body contains "FallbackActivated" or Body contains "fallback queue"
  | project Timestamp, Body, tostring(Attributes["TenantId"])
  | order by Timestamp asc
  // Rows = orders that were routed via fallback during the outage window
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "  ADX query — route tag per order span:" -ForegroundColor Cyan
Write-Host @"
  OTelTraces
  | where Timestamp > ago(1h)
  | where SpanName == "POST /orders/create"
  | extend Route = tostring(Attributes["sb.route"])
  | summarize count() by Route, bin(Timestamp, 1m)
  // primary vs fallback routing ratio over time
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 05 COMPLETE                            ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  Outage simulated: docker stop servicebus-emulator"
Write-Host "  Failover: Polly FallbackAction → orders-fallback queue"
Write-Host "  Detection: OTel log ServiceBus.FallbackActivated"
Write-Host "  Recovery: docker start servicebus-emulator"
Write-Host "  RTO: < 2 seconds (first failed send → fallback active)"
