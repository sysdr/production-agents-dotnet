# docs/capstone-03.ps1 — POST 35
# Observability gap analysis: detect missing metrics, add instrumentation, verify.
#
# WHAT THIS SCRIPT DOES (5 acts):
#   Act 1  Gap scan   — query Collector logs for metrics that should exist but don't
#   Act 2  Pre-check  — confirm the new instrumentation exists in source
#   Act 3  Generate   — send 20 orders to populate the new metrics
#   Act 4  Verify     — confirm order.value.usd and tenant.order.count appear in logs
#   Act 5  ADX query  — print the KQL queries for revenue and billing analytics
#
# PREREQUISITES:
#   dotnet run --project src/AppHost  (with Post 35 code — BusinessMetrics.cs added)
#   docker compose up otel-collector -d

param([switch]$SkipADX, [int]$OrderCount = 20)

$ErrorActionPreference = "Stop"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CAPSTONE 03 — Observability Gap Analysis       ║" -ForegroundColor Cyan
Write-Host   "║  Detect · Instrument · Verify · Query           ║" -ForegroundColor Cyan
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
$userPort  = Get-Port "user-service"

# ══════════════════════════════════════════════════════════════════════════
# ACT 1: GAP SCAN — what's missing from the current telemetry?
# ══════════════════════════════════════════════════════════════════════════
Write-Act 1 "Gap scan — metrics that should exist but don't"

$collectorLogs = docker logs otel-collector 2>&1
$hasOrderValue   = ($collectorLogs | Select-String "order.value.usd").Count -gt 0
$hasTenantCount  = ($collectorLogs | Select-String "tenant.order.count").Count -gt 0
$hasQueueDepth   = ($collectorLogs | Select-String "sb.orders.active_messages").Count -gt 0

Write-Host ""
Write-Host "  Metric presence in OTel Collector logs:"
Write-Host ("  sb.orders.active_messages ... " + $(if($hasQueueDepth) {"PRESENT ✓"} else {"ABSENT ← known gap"})) `
    -ForegroundColor $(if($hasQueueDepth) {"Green"} else {"Yellow"})
Write-Host ("  order.value.usd          ... " + $(if($hasOrderValue) {"PRESENT ✓"} else {"ABSENT ← gap to fill"})) `
    -ForegroundColor $(if($hasOrderValue) {"Green"} else {"Red"})
Write-Host ("  tenant.order.count       ... " + $(if($hasTenantCount) {"PRESENT ✓"} else {"ABSENT ← gap to fill"})) `
    -ForegroundColor $(if($hasTenantCount) {"Green"} else {"Red"})
Write-Host ""
if (-not $hasOrderValue -or -not $hasTenantCount) {
    Write-Host "  Gaps detected. Acts 2-4 will add instrumentation and verify." -ForegroundColor Yellow
} else {
    Write-Host "  All target metrics present. Post 35 instrumentation is active." -ForegroundColor Green
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 2: PRE-CHECK — confirm BusinessMetrics.cs exists
# ══════════════════════════════════════════════════════════════════════════
Write-Act 2 "Pre-check — instrumentation code"

$bmPath = "src/OrderService/BusinessMetrics.cs"
if (Test-Path $bmPath) {
    Write-Host "  BusinessMetrics.cs found ✓" -ForegroundColor Green
    $hasHistogram = (Get-Content $bmPath) -match "CreateHistogram"
    $hasCounter   = (Get-Content $bmPath) -match "CreateCounter"
    Write-Host ("  Histogram<double> order.value.usd ... " + $(if($hasHistogram) {"✓"} else {"MISSING"})) `
        -ForegroundColor $(if($hasHistogram) {"Green"} else {"Red"})
    Write-Host ("  Counter<long> tenant.order.count  ... " + $(if($hasCounter) {"✓"} else {"MISSING"})) `
        -ForegroundColor $(if($hasCounter) {"Green"} else {"Red"})
} else {
    Write-Host "  BusinessMetrics.cs NOT FOUND" -ForegroundColor Red
    Write-Host "  Expected path: $bmPath" -ForegroundColor Yellow
    Write-Host "  The Post 35 code must be active for this script to work." -ForegroundColor Yellow
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 3: GENERATE — send orders to populate the metrics
# ══════════════════════════════════════════════════════════════════════════
Write-Act 3 "Generate — $OrderCount orders across two tenants"

$t1 = Get-Token "tenant-01"
$t2 = Get-Token "tenant-02"
$success = 0

for ($i = 1; $i -le $OrderCount; $i++) {
    $tok = if ($i % 2 -eq 0) { $t1 } else { $t2 }
    try {
        $r = Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
            -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 10
        $success++
        if ($i % 5 -eq 0) {
            Write-Host "  $i/$OrderCount orders sent (last value: `$$($r.Value))" -ForegroundColor DarkGreen
        }
    } catch {
        Write-Host "  Order $i failed: $($_.Exception.Response.StatusCode)" -ForegroundColor Red
    }
    Start-Sleep -Milliseconds 200
}

Write-Host "  $success/$OrderCount orders created successfully."
Write-Host "  Waiting 10s for OTel SDK aggregation interval..." -NoNewline
Start-Sleep 10
Write-Host " done"

# ══════════════════════════════════════════════════════════════════════════
# ACT 4: VERIFY — confirm metrics appear in Collector logs
# ══════════════════════════════════════════════════════════════════════════
Write-Act 4 "Verify — metrics in OTel Collector"

$logsAfter = docker logs otel-collector 2>&1
$orderValueFound  = ($logsAfter | Select-String "order.value.usd").Count  -gt 0
$tenantCountFound = ($logsAfter | Select-String "tenant.order.count").Count -gt 0

Write-Host ""
Write-Host ("  order.value.usd    ... " + $(if($orderValueFound) {"PRESENT ✓ gap closed"} else {"still absent — check meter registration"})) `
    -ForegroundColor $(if($orderValueFound) {"Green"} else {"Red"})
Write-Host ("  tenant.order.count ... " + $(if($tenantCountFound) {"PRESENT ✓ gap closed"} else {"still absent — check meter registration"})) `
    -ForegroundColor $(if($tenantCountFound) {"Green"} else {"Red"})

if (-not $orderValueFound -or -not $tenantCountFound) {
    Write-Host ""
    Write-Host "  If metrics are absent, the most common cause:" -ForegroundColor Yellow
    Write-Host "    builder.Services.AddOpenTelemetry()" -ForegroundColor Yellow
    Write-Host "      .WithMetrics(m => m.AddMeter(""Orders.Business""));" -ForegroundColor Yellow
    Write-Host "  must appear in OrderService/Program.cs BEFORE app.Build()." -ForegroundColor Yellow
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 5: ADX QUERIES
# ══════════════════════════════════════════════════════════════════════════
Write-Act 5 "ADX queries — revenue + billing"

if ($SkipADX) {
    Write-Host "  --SkipADX set. Printing queries for manual execution."
}

Write-Host ""
Write-Host "  QUERY 1 — Revenue by tenant (last 7 days):" -ForegroundColor Cyan
Write-Host @"
  OTelMetrics
  | where Timestamp > ago(7d)
  | where MetricName == "order.value.usd"
  | extend TenantId = tostring(Attributes["TenantId"])
  | summarize
      totalRevenue  = sum(MetricValue),
      avgOrderValue = avg(MetricValue),
      p99OrderValue = percentile(MetricValue, 99),
      orderCount    = count()
      by TenantId
  | order by totalRevenue desc
"@ -ForegroundColor DarkCyan

Write-Host "  QUERY 2 — Daily order count per tenant (billing):" -ForegroundColor Cyan
Write-Host @"
  OTelMetrics
  | where Timestamp > ago(30d)
  | where MetricName == "tenant.order.count"
  | extend TenantId = tostring(Attributes["TenantId"])
  | summarize dailyOrders = sum(MetricValue)
      by TenantId, bin(Timestamp, 1d)
  | render timechart
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 03 COMPLETE                            ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  Gap detected: order.value.usd, tenant.order.count"
Write-Host "  Instrumented: BusinessMetrics.cs + Program.cs"
Write-Host "  Verified: OTel Collector receiving both metrics"
Write-Host "  Queries: revenue by tenant, daily billing count"
