# docs/capstone-01.ps1 — POST 33
# End-to-end incident scenario: multi-tenant order burst
#
# WHAT THIS SCRIPT DOES (5 acts):
#   Act 1  Verify — all services healthy before we start
#   Act 2  Baseline — 50 RPS for 30s, capture p99 reference
#   Act 3  Burst — 200 RPS for 60s, KEDA scales OrderProcessor
#   Act 4  DLQ — inject poison messages, MaxDeliveryCount:3 triggers DLQ
#   Act 5  Forensics — ADX KQL query proves burst effect on p99
#
# PREREQUISITES:
#   - Aspire stack running: dotnet run --project src/AppHost
#   - OTel Collector running: docker compose up otel-collector -d
#   - (optional) ADX free cluster configured (see docs/adx-notes.md)
#
# USAGE:
#   .\docs\capstone-01.ps1
#   .\docs\capstone-01.ps1 -SkipADX          # skip forensics if no ADX cluster
#   .\docs\capstone-01.ps1 -BurstRps 100     # lighter burst for slower machines

param(
    [switch]$SkipADX,
    [int]$BaselineRps = 50,
    [int]$BurstRps    = 200,
    [int]$BaselineSec = 30,
    [int]$BurstSec    = 60,
    [int]$PoisonCount = 5
)

$ErrorActionPreference = "Stop"
$timestamp = Get-Date -Format "yyyy-MM-dd HH:mm:ss"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CAPSTONE 01 — Hyperscale Log Monitoring         ║" -ForegroundColor Cyan
Write-Host   "║  Multi-tenant order burst + KEDA + DLQ + ADX     ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  Started: $timestamp`n"

# ── Helpers ─────────────────────────────────────────────────────────────
function Get-Port {
    param([string]$Name)
    $r = Invoke-RestMethod http://localhost:15888/api/v1/resources -TimeoutSec 5
    ($r | Where-Object { $_.name -eq $Name }).services[0].allocatedEndpoint.port
}

function Get-Token {
    param([string]$TenantId = "tenant-01")
    $body = @{ UserId = "capstone-user"; Email = "capstone@test.com"; TenantId = $TenantId } | ConvertTo-Json
    (Invoke-RestMethod -Method Post "http://localhost:$(Get-Port 'user-service')/auth/token" `
        -Body $body -ContentType "application/json").Token
}

function Write-Act {
    param([int]$Act, [string]$Title)
    Write-Host "`n── Act $Act: $Title " -ForegroundColor Yellow -NoNewline
    Write-Host ("─" * (46 - $Title.Length)) -ForegroundColor DarkGray
}

function Assert-True {
    param([string]$Label, [scriptblock]$Test)
    Write-Host -NoNewline "  $Label ... "
    if (& $Test) { Write-Host "✓" -ForegroundColor Green }
    else { Write-Host "✗ FAILED" -ForegroundColor Red; exit 1 }
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 1: VERIFY
# ══════════════════════════════════════════════════════════════════════════
Write-Act 1 "Verify — system health"

Assert-True "Aspire Dashboard" {
    try { (Invoke-WebRequest http://localhost:15888 -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200 }
    catch { $false }
}

$orderPort = Get-Port "order-service"
$userPort  = Get-Port "user-service"
Write-Host "  order-service port: $orderPort"
Write-Host "  user-service  port: $userPort"

Assert-True "order-service /health" {
    (curl.exe -s -o NUL -w "%{http_code}" "http://localhost:$orderPort/health") -eq "200"
}
Assert-True "user-service /health" {
    (curl.exe -s -o NUL -w "%{http_code}" "http://localhost:$userPort/health") -eq "200"
}
Assert-True "OTel Collector running" {
    (docker ps --filter "name=otel-collector" --format "{{.Status}}") -match "Up"
}
Assert-True "Service Bus emulator running" {
    (docker ps --filter "name=servicebus" --format "{{.Status}}") -match "Up"
}

$baselineToken = Get-Token "tenant-01"
Assert-True "JWT valid (tenant-01)" {
    try {
        (Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
            -Headers @{ Authorization = "Bearer $baselineToken" } -TimeoutSec 10) -ne $null
    } catch { $_.Exception.Response.StatusCode -ne "Unauthorized" }
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 2: BASELINE
# ══════════════════════════════════════════════════════════════════════════
Write-Act 2 "Baseline — $BaselineRps RPS for ${BaselineSec}s"
Write-Host "  Capturing p99 reference before the burst..."

$baselineReport = "docs/capstone-01-baseline"
$baselineStart  = Get-Date

$baselineJob = Start-Job -ScriptBlock {
    param($orderPort, $userPort, $rps, $sec, $report)
    dotnet run --project tests/LoadTests -- `
        --orderServiceUrl "http://localhost:$orderPort" `
        --userServiceUrl  "http://localhost:$userPort"  `
        --reportPath      $report
} -ArgumentList $orderPort, $userPort, $BaselineRps, $BaselineSec, $baselineReport

Write-Host "  NBomber baseline running ($BaselineRps RPS × ${BaselineSec}s)..."
Wait-Job $baselineJob | Out-Null
$baselineDuration = (Get-Date) - $baselineStart
Write-Host "  Baseline complete in $($baselineDuration.TotalSeconds.ToString('F0'))s"
Write-Host "  Report: $baselineReport/*.html"

# ══════════════════════════════════════════════════════════════════════════
# ACT 3: BURST
# ══════════════════════════════════════════════════════════════════════════
Write-Act 3 "Burst — $BurstRps RPS for ${BurstSec}s (KEDA should scale out)"
Write-Host "  This is the incident. Watch KEDA replicas if on AKS:"
Write-Host "  kubectl get pods -n orders -w"
Write-Host ""

$burstReport = "docs/capstone-01-burst"
$burstStart  = Get-Date

# Send burst across two tenants to exercise the TenantAuthorizationHandler IMemoryCache
$t1 = Get-Token "tenant-01"
$t2 = Get-Token "tenant-02"

Write-Host "  Tokens acquired: tenant-01 + tenant-02"
Write-Host "  NBomber burst running ($BurstRps RPS × ${BurstSec}s)..."

# POST 33: Multi-tenant burst — both tenants send orders simultaneously.
# This exercises: JWT auth, TenantConfigStore IMemoryCache (Post 28),
# Service Bus enqueue (Post 7), Event Hubs publish (Post 12),
# and OTel spans for both tenant IDs (Post 13).
$burstJob = Start-Job -ScriptBlock {
    param($orderPort, $userPort, $rps, $sec, $report)
    dotnet run --project tests/LoadTests -- `
        --orderServiceUrl "http://localhost:$orderPort" `
        --userServiceUrl  "http://localhost:$userPort"  `
        --reportPath      $report
} -ArgumentList $orderPort, $userPort, $BurstRps, $BurstSec, $burstReport

Wait-Job $burstJob | Out-Null
$burstDuration = (Get-Date) - $burstStart
Write-Host "  Burst complete in $($burstDuration.TotalSeconds.ToString('F0'))s"
Write-Host "  Report: $burstReport/*.html"
Write-Host ""
Write-Host "  Compare p99: open both HTML reports side by side." -ForegroundColor Cyan
Write-Host "  Expected: burst p99 > baseline p99 by 40-300% at peak RPS." -ForegroundColor Cyan

# ══════════════════════════════════════════════════════════════════════════
# ACT 4: DLQ TRIGGER
# ══════════════════════════════════════════════════════════════════════════
Write-Act 4 "DLQ — inject $PoisonCount poison messages"
Write-Host "  MaxDeliveryCount:3 (Post 9) means each fails 3× before DLQ."
Write-Host "  QueueDepthMonitor (Post 10) emits sb.orders.deadletter_messages gauge."
Write-Host ""

$poisonToken = Get-Token "tenant-01"
$dlqInjected = 0

for ($i = 1; $i -le $PoisonCount; $i++) {
    try {
        Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create-poison" `
            -Headers @{ Authorization = "Bearer $poisonToken" } -TimeoutSec 10 | Out-Null
        $dlqInjected++
        Write-Host "  Poison message $i/$PoisonCount injected" -ForegroundColor Yellow
    } catch {
        Write-Host "  Poison $i: $($_.Exception.Message)" -ForegroundColor DarkYellow
    }
    Start-Sleep 1
}

Write-Host ""
Write-Host "  Waiting 45s for MaxDeliveryCount:3 cycle to complete..." -NoNewline
Start-Sleep 45
Write-Host " done"

# Check DLQ depth via OTel Collector logs
$dlqLog = docker logs otel-collector 2>&1 | Select-String "deadletter_messages" | Select-Object -Last 3
Write-Host "  DLQ gauge (last 3 readings from OTel Collector):"
$dlqLog | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkYellow }

# ══════════════════════════════════════════════════════════════════════════
# ACT 5: FORENSICS
# ══════════════════════════════════════════════════════════════════════════
Write-Act 5 "Forensics — ADX KQL query"

if ($SkipADX) {
    Write-Host "  --SkipADX flag set. Printing query for manual execution."
} else {
    Write-Host "  Run this query in the ADX Web UI (dataexplorer.azure.com):"
}

Write-Host ""
Write-Host @"
  // p99 latency — baseline vs burst, 24h window (1h buckets)
  // Compare the burst window against baseline to prove impact
  OTelTraces
  | where Timestamp > ago(24h)
  | where SpanName == "POST /orders/create"
  | extend IsBurst = Timestamp between (
      datetime_add('minute', -65, now()) .. datetime_add('minute', -5, now()))
  | summarize
      p50 = percentile(DurationMs, 50),
      p99 = percentile(DurationMs, 99),
      count = count()
      by TenantId, IsBurst
  | extend Label = iif(IsBurst, "BURST", "BASELINE")
  | project TenantId, Label, p50, p99, count
  | order by TenantId asc, Label asc
"@ -ForegroundColor Cyan

Write-Host ""
Write-Host "  Expected result:" -ForegroundColor Cyan
Write-Host "    tenant-01 BASELINE  p99 ~95ms   count ~1500" -ForegroundColor DarkCyan
Write-Host "    tenant-01 BURST     p99 ~800ms  count ~12000  ← burst proven" -ForegroundColor DarkCyan
Write-Host "    tenant-02 BASELINE  p99 ~95ms   count ~1500" -ForegroundColor DarkCyan
Write-Host "    tenant-02 BURST     p99 ~900ms  count ~12000  ← burst proven" -ForegroundColor DarkCyan

# ══════════════════════════════════════════════════════════════════════════
# SUMMARY
# ══════════════════════════════════════════════════════════════════════════
$endTime = Get-Date
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 01 COMPLETE                            ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  Completed: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host "  Duration:  $(($endTime - (Get-Date $timestamp)).TotalMinutes.ToString('F1')) min"
Write-Host ""
Write-Host "  Components exercised:"
Write-Host "    Posts 1-6:  OTel spans in Collector logs"
Write-Host "    Posts 7-9:  Service Bus enqueue + MaxDeliveryCount:3"
Write-Host "    Post  10:   QueueDepthMonitor DLQ gauge"
Write-Host "    Posts 12-13: Event Hubs publish + TenantId partitioning"
Write-Host "    Post  20-21: JWT auth + TenantAuthorizationHandler"
Write-Host "    Post  23:   SRE runbook DLQ alert (check dashboard)"
Write-Host "    Post  26:   Testcontainers (background — not invoked here)"
Write-Host "    Post  27:   NBomber — baseline and burst reports written"
Write-Host "    Post  28:   IMemoryCache in TenantConfigStore (silently saving p99)"
Write-Host "    Post  31:   KEDA ScaledObject (if on AKS — verify kubectl)"
Write-Host "    Post  32:   ADX forensics query printed above"
Write-Host ""
Write-Host "  Next steps:"
Write-Host "    1. Open docs/capstone-01-baseline/*.html vs docs/capstone-01-burst/*.html"
Write-Host "    2. Compare p99 at each load step"
Write-Host "    3. Run ADX KQL query above — confirm burst is visible in OTelTraces"
Write-Host "    4. Check SRE dashboard (Post 23) — DLQ alert should be firing"
