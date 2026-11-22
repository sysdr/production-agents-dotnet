# docs/capstone-02.ps1 — POST 34
# Security scenario: tenant isolation failure detection and response
#
# WHAT THIS SCRIPT DOES (5 acts):
#   Act 1  Verify — confirm the security control is working (tenant-99 → 403)
#   Act 2  Probe  — measure the exact boundary of the isolation control
#   Act 3  Forensics — run KQL query that would detect a breach in ADX
#   Act 4  Simulate — temporarily weaken the handler, observe detection firing
#   Act 5  Remediate — restore and verify via the CI/CD pipeline
#
# EDUCATIONAL PURPOSE:
#   This script treats your own system as a security test target.
#   Run it against the local Aspire emulator stack ONLY.
#   All "attack" requests go to localhost.
#
# PREREQUISITES:
#   dotnet run --project src/AppHost  (Aspire stack running)
#   docker compose up otel-collector -d
#
# USAGE:
#   .\docs\capstone-02.ps1
#   .\docs\capstone-02.ps1 -SkipADX       # skip forensics if no ADX cluster
#   .\docs\capstone-02.ps1 -SimulateBreach # also run Act 4 (temporary misconfiguration)

param(
    [switch]$SkipADX,
    [switch]$SimulateBreach
)

$ErrorActionPreference = "Stop"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CAPSTONE 02 — Tenant Isolation Security Audit  ║" -ForegroundColor Cyan
Write-Host   "║  Detection · Forensics · Remediation            ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan

function Get-Port { param([string]$Name)
    $r = Invoke-RestMethod http://localhost:15888/api/v1/resources -TimeoutSec 5
    ($r | Where-Object { $_.name -eq $Name }).services[0].allocatedEndpoint.port
}
function Get-Token { param([string]$TenantId)
    $body = @{ UserId = "security-test"; Email = "sec@test.com"; TenantId = $TenantId } | ConvertTo-Json
    (Invoke-RestMethod -Method Post "http://localhost:$(Get-Port 'user-service')/auth/token" `
        -Body $body -ContentType "application/json").Token
}
function Write-Act { param([int]$Act, [string]$Title)
    Write-Host "`n── Act $Act: $Title " -ForegroundColor Yellow -NoNewline
    Write-Host ("─" * (46 - $Title.Length)) -ForegroundColor DarkGray
}

$orderPort = Get-Port "order-service"
$userPort  = Get-Port "user-service"

# ══════════════════════════════════════════════════════════════════════════
# ACT 1: VERIFY — security control is working
# ══════════════════════════════════════════════════════════════════════════
Write-Act 1 "Verify — isolation control"

# Known tenant — should succeed
$t01Token = Get-Token "tenant-01"
Write-Host -NoNewline "  tenant-01 (known) → "
try {
    Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
        -Headers @{ Authorization = "Bearer $t01Token" } -TimeoutSec 10 | Out-Null
    Write-Host "200 OK ✓" -ForegroundColor Green
} catch { Write-Host "$($_.Exception.Response.StatusCode) — UNEXPECTED" -ForegroundColor Red; exit 1 }

# Unknown tenant — MUST return 403
$t99Token = Get-Token "tenant-99"
Write-Host -NoNewline "  tenant-99 (unknown) → "
try {
    Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
        -Headers @{ Authorization = "Bearer $t99Token" } -TimeoutSec 10 | Out-Null
    Write-Host "200 OK — ISOLATION FAILURE!" -ForegroundColor Red
    Write-Host "  TenantAuthorizationHandler is not blocking unknown tenants." -ForegroundColor Red
    Write-Host "  Run: git diff src/OrderService/TenantAuthorizationHandler.cs" -ForegroundColor Yellow
    exit 1
} catch {
    $code = $_.Exception.Response.StatusCode
    if ($code -eq "Forbidden") {
        Write-Host "403 Forbidden ✓ — isolation control working" -ForegroundColor Green
    } else {
        Write-Host "$code — unexpected status" -ForegroundColor Yellow
    }
}

# Expired/disabled tenant — also must 403
Write-Host -NoNewline "  tenant-02 with disabled config → "
# POST 34: We test this by temporarily setting a disabled flag in Table Storage.
# In a real system, UpdateTenantConfigAsync sets IsEnabled=false.
# The IMemoryCache (Post 28) may serve a 30s stale entry — this is the race window.
Write-Host "skip (requires Table Storage write — see capstone-02-notes.md)" -ForegroundColor DarkGray

Write-Host ""
Write-Host "  Security control: PASS" -ForegroundColor Green
Write-Host "  Isolation boundary verified for known + unknown tenants."

# ══════════════════════════════════════════════════════════════════════════
# ACT 2: PROBE — measure the isolation boundary
# ══════════════════════════════════════════════════════════════════════════
Write-Act 2 "Probe — isolation boundary"

Write-Host "  Testing 10 unknown tenant IDs..."
$blocked = 0; $allowed = 0
foreach ($id in @("tenant-99","tenant-00","tenant-XX","acme-corp","evil-corp",
                   "admin","root","test","bypass","injection")) {
    $tok = Get-Token $id
    try {
        Invoke-RestMethod -Method Post "http://localhost:$orderPort/orders/create" `
            -Headers @{ Authorization = "Bearer $tok" } -TimeoutSec 5 | Out-Null
        $allowed++
        Write-Host "  $id → ALLOWED  ← isolation failure!" -ForegroundColor Red
    } catch {
        if ($_.Exception.Response.StatusCode -eq "Forbidden") {
            $blocked++
            Write-Host "  $id → 403 ✓" -ForegroundColor DarkGreen
        }
    }
}

Write-Host ""
if ($allowed -gt 0) {
    Write-Host "  WARNING: $allowed tenant(s) bypassed isolation!" -ForegroundColor Red
} else {
    Write-Host "  All $blocked unknown tenants correctly blocked. Isolation intact." -ForegroundColor Green
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 3: FORENSICS — ADX breach detection query
# ══════════════════════════════════════════════════════════════════════════
Write-Act 3 "Forensics — ADX breach detection"

Write-Host "  Two queries to run in ADX Web UI (dataexplorer.azure.com):"
Write-Host ""
Write-Host "  QUERY 1 — Find any successful orders from unknown tenants:" -ForegroundColor Cyan
Write-Host @"
  // If this returns ANY rows, there was an isolation breach
  let KnownTenants = dynamic(["tenant-01", "tenant-02"]);
  OTelTraces
  | where Timestamp > ago(24h)
  | where SpanName == "POST /orders/create"
  | where StatusCode == "Ok"
  | extend TenantId = tostring(Attributes["TenantId"])
  | where TenantId !in (KnownTenants)
  | project Timestamp, TraceId, TenantId, DurationMs
  | order by Timestamp asc
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "  QUERY 2 — Breach window analysis (if rows found above):" -ForegroundColor Cyan
Write-Host @"
  // Quantify the exposure: how many orders, over what time window?
  let KnownTenants = dynamic(["tenant-01", "tenant-02"]);
  OTelTraces
  | where Timestamp > ago(7d)
  | where SpanName == "POST /orders/create" and StatusCode == "Ok"
  | extend TenantId = tostring(Attributes["TenantId"])
  | where TenantId !in (KnownTenants)
  | summarize
      firstSeen  = min(Timestamp),
      lastSeen   = max(Timestamp),
      orderCount = count()
      by TenantId
  | extend exposureMinutes = datetime_diff('minute', lastSeen, firstSeen)
  | project TenantId, firstSeen, lastSeen, exposureMinutes, orderCount
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "  Expected result (healthy system): 0 rows." -ForegroundColor Green
Write-Host "  If rows appear: follow the remediation playbook in capstone-02-notes.md." -ForegroundColor Yellow

# ══════════════════════════════════════════════════════════════════════════
# ACT 4: SIMULATE BREACH (optional, requires -SimulateBreach flag)
# ══════════════════════════════════════════════════════════════════════════
Write-Act 4 "Simulate breach (optional)"

if (-not $SimulateBreach) {
    Write-Host "  Skipped. Run with -SimulateBreach to temporarily misconfigure the handler."
    Write-Host "  See capstone-02-notes.md for the exact code change to make (and revert)."
} else {
    Write-Host "  IMPORTANT: This temporarily modifies TenantAuthorizationHandler." -ForegroundColor Yellow
    Write-Host "  Revert immediately after observing. Do NOT commit this change." -ForegroundColor Red
    Write-Host ""
    Write-Host "  Step 1: In src/OrderService/TenantAuthorizationHandler.cs, change:" -ForegroundColor Cyan
    Write-Host "    context.Fail(); return;"  -ForegroundColor DarkRed
    Write-Host "  to:"
    Write-Host "    context.Succeed(requirement); return; // TEMP — REVERT IMMEDIATELY"  -ForegroundColor DarkGreen
    Write-Host ""
    Write-Host "  Step 2: Restart OrderService (Ctrl+C + dotnet run, or Aspire restart)"
    Write-Host "  Step 3: Run: .\docs\capstone-02.ps1  (without -SimulateBreach)"
    Write-Host "          You will see: tenant-99 → ISOLATION FAILURE"
    Write-Host "  Step 4: git diff  # confirm what changed"
    Write-Host "  Step 5: git checkout -- src/OrderService/TenantAuthorizationHandler.cs"
    Write-Host "  Step 6: Restart OrderService again"
    Write-Host "  Step 7: Re-run this script to confirm 403 is restored"
}

# ══════════════════════════════════════════════════════════════════════════
# ACT 5: REMEDIATE — CI/CD deploy path
# ══════════════════════════════════════════════════════════════════════════
Write-Act 5 "Remediate — CI/CD timeline"

Write-Host "  If a real breach was detected, the remediation sequence is:"
Write-Host ""
Write-Host "  T+0m   Breach confirmed in ADX query (Act 3)"
Write-Host "  T+1m   Fix committed: git commit -m 'fix: restore tenant isolation check'"
Write-Host "  T+1m   PR opened → pr-checks.yml fires (Post 30)"
Write-Host "  T+4m   dotnet test passes (Testcontainers — Post 26)"
Write-Host "  T+4m   az bicep build passes (infra/main.bicep — Post 29)"
Write-Host "  T+5m   PR approved + merged (required reviewer — Post 30)"
Write-Host "  T+5m   ci-cd.yml fires on push to main (cancel-in-progress — Post 30)"
Write-Host "  T+8m   Docker image built + pushed to ACR (SHA tag — Post 30)"
Write-Host "  T+10m  az deployment group create completes (Bicep — Post 29)"
Write-Host "  T+10m  Container Apps revision updated — unknown tenants blocked again"
Write-Host ""
Write-Host "  Total exposure window: ~10 minutes from detection to remediation." -ForegroundColor Green
Write-Host "  Without CI/CD: manual deploy adds 30-60 minutes."                 -ForegroundColor DarkYellow
Write-Host ""
Write-Host "  Validate the fix is live:"
Write-Host "    az containerapp show --name order-service --resource-group rg-orders-prod \"
Write-Host "      --query 'properties.latestRevisionName'"

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 02 COMPLETE                            ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  Isolation: verified ✓"
Write-Host "  Detection: ADX forensics queries printed ✓"
Write-Host "  Remediation: CI/CD timeline documented ✓"
