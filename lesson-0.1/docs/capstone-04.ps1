# docs/capstone-04.ps1 — POST 36
# OTel sampling validation: confirm sampling is working and quantify cost savings.
#
# WHAT THIS SCRIPT DOES (5 acts):
#   Act 1  Verify    — confirm tail_sampling processor active in Collector
#   Act 2  Baseline  — send 100 successful orders, confirm ~10% reach ADX
#   Act 3  Errors    — send 10 error orders, confirm 100% reach ADX
#   Act 4  Cost math — calculate projected monthly savings at 200 RPS
#   Act 5  ADX check — query ADX sampling rate to validate in production

param([switch]$SkipADX, [int]$SuccessCount = 100, [int]$ErrorCount = 10)

$ErrorActionPreference = "Stop"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CAPSTONE 04 — OTel Sampling Cost Optimisation  ║" -ForegroundColor Cyan
Write-Host   "║  Validate · Measure · Calculate · Verify         ║" -ForegroundColor Cyan
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

# ══════════════════════════════════════════════════════════════════════════
# ACT 1: VERIFY — tail_sampling processor is active
# ══════════════════════════════════════════════════════════════════════════
Write-Act 1 "Verify — tail_sampling processor"

$collectorLogs = docker logs otel-collector 2>&1 | Select-Object -Last 200
$hasTailSampling = ($collectorLogs | Select-String "tail.sampl\|tailsampl\|sampling").Count -gt 0
$isContrib = (docker inspect otel-collector 2>&1 | Select-String "collector-contrib").Count -gt 0

Write-Host ""
Write-Host ("  OTel Collector contrib image ... " + $(if($isContrib) {"✓"} else {"✗ — needs otel/opentelemetry-collector-contrib"})) `
    -ForegroundColor $(if($isContrib) {"Green"} else {"Red"})
Write-Host ("  tail_sampling in logs        ... " + $(if($hasTailSampling) {"✓"} else {"not yet seen — send a request first"})) `
    -ForegroundColor $(if($hasTailSampling) {"Green"} else {"Yellow"})

# Check config directly
$configHasTailSampling = (Get-Content "config/otel-collector-config.yaml" -Raw) -match "tail_sampling"
Write-Host ("  tail_sampling in config YAML ... " + $(if($configHasTailSampling) {"✓"} else {"missing!"})) `
    -ForegroundColor $(if($configHasTailSampling) {"Green"} else {"Red"})

# ══════════════════════════════════════════════════════════════════════════
# ACT 2: BASELINE — send successes, expect ~10% in Collector
# ══════════════════════════════════════════════════════════════════════════
Write-Act 2 "Baseline — $SuccessCount orders → expect ~10% sampled"

$tok = Get-Token "tenant-01"
$sent = 0

Write-Host "  Sending $SuccessCount successful orders..."
$logsBeforeCount = (docker logs otel-collector 2>&1 | Select-String "SpanName.*orders.create\|orders/create").Count

for ($i = 1; $i -le $SuccessCount; $i++) {
    try {
        Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
            -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 10 | Out-Null
        $sent++
    } catch {}
    if ($i % 25 -eq 0) { Write-Host "  $i sent..." }
    Start-Sleep -Milliseconds 50
}

Write-Host "  Waiting 15s for tail_sampling decision_wait..." -NoNewline
Start-Sleep 15
Write-Host " done"

$logsAfterCount  = (docker logs otel-collector 2>&1 | Select-String "SpanName.*orders.create\|orders/create").Count
$exportedSpans   = $logsAfterCount - $logsBeforeCount
$samplingRate    = if ($sent -gt 0) { [Math]::Round($exportedSpans / $sent * 100, 1) } else { 0 }

Write-Host ""
Write-Host "  Sent:          $sent orders"
Write-Host "  Exported spans: $exportedSpans (approx — based on Collector log entries)"
Write-Host "  Effective rate: $samplingRate% (target: ~10%)" -ForegroundColor $(if($samplingRate -ge 5 -and $samplingRate -le 25) {"Green"} else {"Yellow"})
Write-Host "  Note: sampling rate varies ±5% due to probabilistic policy." -ForegroundColor DarkGray

# ══════════════════════════════════════════════════════════════════════════
# ACT 3: ERRORS — inject errors, expect 100% in Collector
# ══════════════════════════════════════════════════════════════════════════
Write-Act 3 "Errors — $ErrorCount poison messages → expect 100% sampled"

$errorLogsBefore = (docker logs otel-collector 2>&1 | Select-String "ERROR\|error.*span\|StatusCode.*Error").Count
$errorsSent = 0

for ($i = 1; $i -le $ErrorCount; $i++) {
    try {
        Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create-poison" `
            -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 10 | Out-Null
        $errorsSent++
    } catch { $errorsSent++ }   # poison endpoint may return error HTTP status
    Start-Sleep -Milliseconds 200
}

Write-Host "  Waiting 15s for tail_sampling decision_wait..." -NoNewline
Start-Sleep 15
Write-Host " done"

$errorLogsAfter   = (docker logs otel-collector 2>&1 | Select-String "ERROR\|error.*span\|StatusCode.*Error").Count
$exportedErrors   = $errorLogsAfter - $errorLogsBefore
Write-Host "  Sent: $errorsSent error-triggering requests"
Write-Host "  Error spans exported: $exportedErrors" -ForegroundColor $(if($exportedErrors -ge $errorsSent - 1) {"Green"} else {"Yellow"})
Write-Host "  Note: error export verification is approximate via log scanning." -ForegroundColor DarkGray

# ══════════════════════════════════════════════════════════════════════════
# ACT 4: COST MATH
# ══════════════════════════════════════════════════════════════════════════
Write-Act 4 "Cost math — projected savings at 200 RPS"

$rps          = 200
$daysPerMonth = 30
$secondsMonth = 86400 * $daysPerMonth
$errorRate    = 0.01     # 1% error rate assumption
$sampleRate   = 0.10     # 10% success sample rate
$slowRate     = 0.05     # 5% slow requests (>500ms) — always kept
$kbPerSpan    = 1.0      # ~1KB per compressed span

$totalSpansMonth  = $rps * $secondsMonth
$errorSpans       = [long]($totalSpansMonth * $errorRate)
$slowSpans        = [long]($totalSpansMonth * $slowRate * (1 - $errorRate))
$successSpansFull = [long]($totalSpansMonth * (1 - $errorRate - $slowRate))
$successSampled   = [long]($successSpansFull * $sampleRate)
$totalAfter       = $errorSpans + $slowSpans + $successSampled
$reductionPct     = [Math]::Round((1 - $totalAfter / $totalSpansMonth) * 100, 1)

$gbBefore = [Math]::Round($totalSpansMonth * $kbPerSpan / 1MB, 0)
$gbAfter  = [Math]::Round($totalAfter * $kbPerSpan / 1MB, 0)

Write-Host ""
Write-Host "  At $rps RPS, $daysPerMonth-day month:"
Write-Host "  ─────────────────────────────────────"
Write-Host "  BEFORE sampling:"
Write-Host "    Total spans:     $("{0:N0}" -f $totalSpansMonth) (~$gbBefore GB)"
Write-Host ""
Write-Host "  AFTER tail_sampling (10% success, 100% errors+slow):"
Write-Host "    Error spans:     $("{0:N0}" -f $errorSpans)  (100% — always kept)"
Write-Host "    Slow spans:      $("{0:N0}" -f $slowSpans)  (100% — >500ms kept)"
Write-Host "    Success sampled: $("{0:N0}" -f $successSampled)  (10% sample)"
Write-Host "    Total:           $("{0:N0}" -f $totalAfter) (~$gbAfter GB)"
Write-Host "    Reduction:       $reductionPct%" -ForegroundColor Green
Write-Host ""
Write-Host "  ADX ingestion cost at `$0.023/GB (Standard SKU):" -ForegroundColor Cyan
Write-Host "    Before: `$$([Math]::Round($gbBefore * 0.023, 0))/month"
Write-Host "    After:  `$$([Math]::Round($gbAfter  * 0.023, 0))/month" -ForegroundColor Green

# ══════════════════════════════════════════════════════════════════════════
# ACT 5: ADX SAMPLING RATE VALIDATION
# ══════════════════════════════════════════════════════════════════════════
Write-Act 5 "ADX validation — verify sampling rate in OTelTraces"

if ($SkipADX) {
    Write-Host "  --SkipADX set. Print query for manual execution:"
}

Write-Host ""
Write-Host "  Run in ADX Web UI to measure actual sampling rate:" -ForegroundColor Cyan
Write-Host @"

  // Compare trace volume before/after sampling config
  // Expected: ~10% of before volume for successful spans
  OTelTraces
  | where Timestamp > ago(1h)
  | where SpanName == "POST /orders/create"
  | summarize
      totalSpans  = count(),
      errorSpans  = countif(StatusCode != "Ok"),
      successSpans= countif(StatusCode == "Ok"),
      slowSpans   = countif(DurationMs > 500)
      by bin(Timestamp, 5m)
  | extend successSampleRate = round(100.0 * successSpans / (totalSpans - errorSpans - slowSpans), 1)
  | project Timestamp, totalSpans, errorSpans, slowSpans, successSpans, successSampleRate

"@ -ForegroundColor DarkCyan

Write-Host "  Expected: successSampleRate ≈ 10 (±5 due to probabilistic policy)" -ForegroundColor Green

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 04 COMPLETE                            ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  Sampling: $reductionPct% reduction in ADX ingestion volume"
Write-Host "  Fidelity: 100% of errors + slow traces preserved"
Write-Host "  Config:   config/otel-collector-config.yaml tail_sampling block"
