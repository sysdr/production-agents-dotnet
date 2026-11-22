# docs/capstone-07.ps1 — POST 39
# Distributed trace correlation: verify TraceId propagates across all four hops.
#
# WHAT THIS SCRIPT PROVES (5 acts):
#   Act 1  Baseline    — confirm TraceContextPropagator.cs is wired into both send/receive sides
#   Act 2  Send order  — create an order, capture the OrderId
#   Act 3  Trace hunt  — grep OTel Collector logs for all 4 spans sharing one TraceId
#   Act 4  Verify link — confirm ProcessOrderMessage and ProcessOrderEvent are CHILD spans
#   Act 5  ADX query   — the "recover full trace by OrderId" query for production use
#
# PREREQUISITES:
#   dotnet run --project src/AppHost  (Aspire stack running, Post 39 code active)
#   docker compose up otel-collector -d

param([switch]$SkipADX)

$ErrorActionPreference = "Stop"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CAPSTONE 07 — Distributed Trace Correlation     ║" -ForegroundColor Cyan
Write-Host   "║  4 hops · 1 TraceId · ADX Recovery              ║" -ForegroundColor Cyan
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
# ACT 1: BASELINE — confirm the propagator is wired in
# ══════════════════════════════════════════════════════════════════════════
Write-Act 1 "Baseline — trace propagation code present"

$hasPropagator = Test-Path "src/ServiceDefaults/TraceContextPropagator.cs"
Write-Host ("  TraceContextPropagator.cs: " + $(if($hasPropagator){"present ✓"}else{"MISSING — Post 39 code required"})) `
    -ForegroundColor $(if($hasPropagator) {"Green"} else {"Red"})

$injectCount = (Select-String -Path "src/OrderService/Program.cs" -Pattern "InjectTraceContext" -ErrorAction SilentlyContinue).Count
Write-Host ("  InjectTraceContext call sites in OrderService: $injectCount (expect 2: SB + EH)") `
    -ForegroundColor $(if($injectCount -eq 2) {"Green"} else {"Yellow"})

$extractSB = (Select-String -Path "src/OrderProcessor/OrderProcessorWorker.cs" -Pattern "ExtractTraceContext" -ErrorAction SilentlyContinue).Count -gt 0
$extractEH = (Select-String -Path "src/OrderEventProcessor/OrderEventProcessorWorker.cs" -Pattern "ExtractTraceContext" -ErrorAction SilentlyContinue).Count -gt 0
Write-Host ("  ExtractTraceContext in OrderProcessor:      " + $(if($extractSB){"✓"}else{"MISSING"})) -ForegroundColor $(if($extractSB){"Green"}else{"Red"})
Write-Host ("  ExtractTraceContext in OrderEventProcessor: " + $(if($extractEH){"✓"}else{"MISSING"})) -ForegroundColor $(if($extractEH){"Green"}else{"Red"})

# ══════════════════════════════════════════════════════════════════════════
# ACT 2: SEND ORDER
# ══════════════════════════════════════════════════════════════════════════
Write-Act 2 "Send order — capture OrderId for trace hunt"

$r = Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
    -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 10
$orderId = $r.OrderId
Write-Host "  Order created: $orderId" -ForegroundColor Green
Write-Host "  Waiting 12s for all 4 hops to process (SB + EH consumers)..." -NoNewline
Start-Sleep 12
Write-Host " done"

# ══════════════════════════════════════════════════════════════════════════
# ACT 3: TRACE HUNT — find all spans sharing one TraceId
# ══════════════════════════════════════════════════════════════════════════
Write-Act 3 "Trace hunt — locate all 4 spans for $orderId"

$logs = docker logs otel-collector 2>&1 | Select-String $orderId

Write-Host "  Collector log lines mentioning $orderId : $($logs.Count)"
$spanNames = @("CreateOrder", "PublishOrderEvent", "ProcessOrderMessage", "ProcessOrderEvent")
$foundSpans = @{}

foreach ($spanName in $spanNames) {
    $found = ($logs | Select-String $spanName).Count -gt 0
    $foundSpans[$spanName] = $found
    Write-Host ("  $spanName" + (" " * (24 - $spanName.Length)) + ": " + $(if($found){"found ✓"}else{"not found"})) `
        -ForegroundColor $(if($found) {"Green"} else {"Yellow"})
}

$allFound = ($foundSpans.Values | Where-Object { $_ -eq $false }).Count -eq 0
Write-Host ""
Write-Host ("  All 4 spans present: " + $(if($allFound){"✓ YES"}else{"partial — some spans missing from local log grep"})) `
    -ForegroundColor $(if($allFound) {"Green"} else {"Yellow"})
Write-Host "  Note: local grep is approximate — ADX query (Act 5) is the authoritative check." -ForegroundColor DarkGray

# ══════════════════════════════════════════════════════════════════════════
# ACT 4: VERIFY PARENT-CHILD LINKING
# ══════════════════════════════════════════════════════════════════════════
Write-Act 4 "Verify link — TraceId consistency across spans"

Write-Host "  Extracting trace_id values from Collector logs for this order..."
$traceIdLines = docker logs otel-collector 2>&1 |
    Select-String $orderId |
    Select-String "trace_id|TraceId|traceId" |
    Select-Object -First 8

if ($traceIdLines) {
    $traceIdLines | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkCyan }
    Write-Host ""
    Write-Host "  Manually confirm: do all lines show the SAME trace_id value?" -ForegroundColor Yellow
    Write-Host "  If yes: correlation is working — one TraceId spans all 4 hops." -ForegroundColor Green
    Write-Host "  If different TraceIds per span: InjectTraceContext/ExtractTraceContext" -ForegroundColor Red
    Write-Host "  is not wired correctly — check Act 1 output above." -ForegroundColor Red
} else {
    Write-Host "  No trace_id lines found in this grep — try the ADX query (Act 5) instead." -ForegroundColor Yellow
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 5: ADX QUERY — recover the full trace by OrderId
# ══════════════════════════════════════════════════════════════════════════
Write-Act 5 "ADX query — full trace recovery by OrderId"

if ($SkipADX) { Write-Host "  --SkipADX set. Printing query for manual execution." }

Write-Host ""
Write-Host "  Query 1 — find the TraceId for a given OrderId:" -ForegroundColor Cyan
Write-Host @"
  OTelTraces
  | where Timestamp > ago(1h)
  | where Attributes["OrderId"] == "$orderId"
  | project TraceId, SpanName, Timestamp
  | order by Timestamp asc
  | take 1
  // Take the TraceId from the first row (CreateOrder span)
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "  Query 2 — recover the FULL trace once you have the TraceId:" -ForegroundColor Cyan
Write-Host @"
  OTelTraces
  | where Timestamp > ago(1h)
  | where TraceId == "<paste TraceId from Query 1>"
  | project Timestamp, SpanName, ServiceName, DurationMs, ParentSpanId
  | order by Timestamp asc
  // Expected 4 rows: CreateOrder, PublishOrderEvent, ProcessOrderMessage, ProcessOrderEvent
  // ParentSpanId chains them: CreateOrder → PublishOrderEvent → {ProcessOrderMessage, ProcessOrderEvent}
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "  Query 3 — one-shot: OrderId to full trace in a single query:" -ForegroundColor Cyan
Write-Host @"
  let targetOrderId = "$orderId";
  let traceId = toscalar(
      OTelTraces
      | where Attributes["OrderId"] == targetOrderId
      | project TraceId
      | take 1);
  OTelTraces
  | where TraceId == traceId
  | project Timestamp, SpanName, ServiceName, DurationMs
  | order by Timestamp asc
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 07 COMPLETE                            ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  OrderId tested: $orderId"
Write-Host "  Propagation:    W3C traceparent via ApplicationProperties/EventData.Properties"
Write-Host "  4 hops:         CreateOrder → PublishOrderEvent → {ProcessOrderMessage, ProcessOrderEvent}"
Write-Host "  ADX recovery:   OrderId → TraceId → full trace (Query 3 above)"
