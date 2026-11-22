# docs/production-checklist.ps1 — POST 42
# Pre-launch production readiness gate: audits every configuration decision
# made across Posts 1-41 against a single pass/fail checklist.
#
# WHAT MAKES THIS DIFFERENT FROM verify.ps1:
#   verify.ps1 (grown across every post) checks that the LOCAL Aspire stack
#   is functioning correctly RIGHT NOW — health endpoints respond, spans
#   appear in the Collector, DLQ triggers work.
#
#   production-checklist.ps1 checks that the DECISIONS embedded in config
#   files are the ones you actually intended to ship — @secure() is present,
#   version pins haven't drifted, minReplicaCount wasn't accidentally set
#   to 0, the CI/CD required-reviewer gate is configured. It does NOT
#   require the Aspire stack to be running — it audits static config,
#   the way a release manager reviews a diff before approving a deploy.
#
# SEVEN CATEGORIES, MATCHING THE SERIES' NINE PHASES:
#   1. Version pinning integrity      (Phase 1-2, all posts)
#   2. Security posture               (Phase 4, Posts 20-21, 34)
#   3. Resilience configuration       (Phase 2/6/10, Posts 9-10, 37, 40)
#   4. Scaling configuration          (Phase 9, Post 31)
#   5. Observability completeness     (Phase 3/9, Posts 15-19, 32, 35-36)
#   6. Infrastructure-as-code hygiene (Phase 8, Posts 29-30)
#   7. Schema evolution safety        (Post 38)
#
# USAGE:
#   .\docs\production-checklist.ps1
#   .\docs\production-checklist.ps1 -Strict   # treat warnings as failures

param([switch]$Strict)

$pass = 0; $warn = 0; $fail = 0
$findings = @()

function Write-Category { param([string]$Name)
    Write-Host "`n━━━ $Name ━━━" -ForegroundColor Magenta
}
function Check-Pass  { param([string]$Msg) Write-Host "  ✓ $Msg" -ForegroundColor Green;  $script:pass++ }
function Check-Warn  { param([string]$Msg) Write-Host "  ⚠ $Msg" -ForegroundColor Yellow; $script:warn++; $script:findings += "WARN: $Msg" }
function Check-Fail  { param([string]$Msg) Write-Host "  ✗ $Msg" -ForegroundColor Red;    $script:fail++; $script:findings += "FAIL: $Msg" }

Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  PRODUCTION DEPLOYMENT CHECKLIST — POST 42       ║" -ForegroundColor Cyan
Write-Host   "║  Static config audit · no running stack required ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 1: VERSION PINNING INTEGRITY
# ══════════════════════════════════════════════════════════════════════════
Write-Category "1. Version Pinning Integrity"

$csprojFiles = Get-ChildItem -Path "src","tests" -Filter "*.csproj" -Recurse -ErrorAction SilentlyContinue
$unpinned = @()
foreach ($f in $csprojFiles) {
    $content = Get-Content $f.FullName -Raw
    # POST 42: A PackageReference with no Version attribute, or Version="*"
    # floats to whatever NuGet resolves at restore time — never acceptable
    # in a production build. Every reference should have an explicit Version.
    $refs = [regex]::Matches($content, '<PackageReference\s+Include="([^"]+)"(?:\s+Version="([^"]*)")?')
    foreach ($m in $refs) {
        $pkg = $m.Groups[1].Value
        $ver = $m.Groups[2].Value
        if ([string]::IsNullOrWhiteSpace($ver) -or $ver -eq "*") {
            $unpinned += "$($f.Name): $pkg has no pinned version"
        }
    }
}
if ($unpinned.Count -eq 0) {
    Check-Pass "All PackageReference entries have explicit pinned versions"
} else {
    $unpinned | ForEach-Object { Check-Fail $_ }
}

# POST 42: Docker image tags — 'latest' is never acceptable for a base image
# in a production Dockerfile or compose file (Post 30's SHA-tagging principle
# extends to base images, not just your own build output).
$composeContent = Get-Content "docker-compose.yml" -Raw -ErrorAction SilentlyContinue
if ($composeContent -match "image:\s*\S+:latest") {
    Check-Fail "docker-compose.yml references a ':latest' tagged image — pin to a specific version"
} else {
    Check-Pass "No ':latest' Docker image tags found in docker-compose.yml"
}

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 2: SECURITY POSTURE
# ══════════════════════════════════════════════════════════════════════════
Write-Category "2. Security Posture"

if (Test-Path "infra/main.bicep") {
    $bicep = Get-Content "infra/main.bicep" -Raw
    if ($bicep -match "@secure\(\)\s*\r?\nparam\s+jwtSigningKey") {
        Check-Pass "jwtSigningKey parameter carries @secure() decorator"
    } else {
        Check-Fail "jwtSigningKey missing @secure() — signing key would be stored in plain text in ARM deployment history"
    }
}

if (Test-Path "src/OrderService/TenantAuthorizationRequirement.cs") {
    $handler = Get-Content "src/OrderService/TenantAuthorizationRequirement.cs" -Raw
    if ($handler -match "context\.Fail\(\)") {
        Check-Pass "TenantAuthorizationHandler explicitly calls context.Fail() on the negative path"
    } else {
        Check-Fail "TenantAuthorizationHandler has no explicit context.Fail() — see Post 34's fall-through vulnerability"
    }
}

if (Test-Path "src/OrderService/OrderService.csproj") {
    $osProj = Get-Content "src/OrderService/OrderService.csproj" -Raw
    if ($osProj -match 'JwtBearer.*Version="8\.0\.0"') {
        Check-Pass "JwtBearer pinned to 8.0.0 (matches project version table)"
    } else {
        Check-Warn "JwtBearer version does not match the pinned 8.0.0 baseline — confirm intentional"
    }
}

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 3: RESILIENCE CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════
Write-Category "3. Resilience Configuration"

if (Test-Path "config/servicebus-config.json") {
    $sbConfig = Get-Content "config/servicebus-config.json" -Raw | ConvertFrom-Json
    $queues = $sbConfig.UserConfig.Namespaces[0].Queues
    foreach ($q in $queues) {
        if ($q.Properties.MaxDeliveryCount -eq 3) {
            Check-Pass "Queue '$($q.Name)': MaxDeliveryCount = 3 (consistent DLQ behaviour)"
        } else {
            Check-Warn "Queue '$($q.Name)': MaxDeliveryCount = $($q.Properties.MaxDeliveryCount) — differs from the series' pinned value of 3"
        }
    }
    $primary  = $queues | Where-Object { $_.Name -eq "orders" }
    $fallback = $queues | Where-Object { $_.Name -eq "orders-fallback" }
    if ($primary -and $fallback) {
        Check-Pass "Both primary ('orders') and fallback ('orders-fallback') queues configured"
    } else {
        Check-Fail "Missing orders-fallback queue — Post 37's Polly FallbackAction has nowhere to route"
    }
}

if (Test-Path "src/OrderService/FallbackServiceBusSender.cs") {
    Check-Pass "FallbackServiceBusSender.cs present (Post 37 failover mechanism)"
} else {
    Check-Fail "FallbackServiceBusSender.cs missing — no Service Bus outage protection"
}

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 4: SCALING CONFIGURATION
# ══════════════════════════════════════════════════════════════════════════
Write-Category "4. Scaling Configuration"

if (Test-Path "k8s/order-processor-scaledobject.yaml") {
    $scaledObj = Get-Content "k8s/order-processor-scaledobject.yaml" -Raw
    if ($scaledObj -match "minReplicaCount:\s*1\b") {
        Check-Pass "minReplicaCount: 1 — no cold-start latency for the order processor"
    } elseif ($scaledObj -match "minReplicaCount:\s*0\b") {
        Check-Warn "minReplicaCount: 0 — order processor will cold-start on first message (20-70s per Post 31's math). Confirm this is acceptable for your SLA."
    } else {
        Check-Fail "minReplicaCount not found in ScaledObject — KEDA scaling is misconfigured"
    }

    if ($scaledObj -match "maxReplicaCount:\s*(\d+)") {
        $bicepMax = if (Test-Path "infra/main.bicep") {
            (Get-Content "infra/main.bicep" -Raw) -match "maxReplicas\s*:\s*10"
        } else { $false }
        Check-Pass "maxReplicaCount configured ($($matches[1]))"
    }

    if ($scaledObj -match "cooldownPeriod:\s*(\d+)") {
        $cooldown = [int]$matches[1]
        if ($cooldown -ge 180) {
            Check-Pass "cooldownPeriod: ${cooldown}s — sufficient to absorb inter-burst gaps without thrashing"
        } else {
            Check-Warn "cooldownPeriod: ${cooldown}s is below the recommended 180s minimum — may cause scale-down/scale-up thrashing"
        }
    }
}

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 5: OBSERVABILITY COMPLETENESS
# ══════════════════════════════════════════════════════════════════════════
Write-Category "5. Observability Completeness"

if (Test-Path "config/otel-collector-config.yaml") {
    $otelConfig = Get-Content "config/otel-collector-config.yaml" -Raw
    if ($otelConfig -match "azuredataexplorer") {
        Check-Pass "ADX dual-export configured (long-retention analytics)"
    } else {
        Check-Warn "No azuredataexplorer exporter — only short-retention telemetry available"
    }
    if ($otelConfig -match "tail_sampling") {
        Check-Pass "tail_sampling processor active — cost-controlled with error/slow-trace fidelity preserved"
    } else {
        Check-Warn "No tail_sampling — telemetry cost will scale linearly with traffic (see Post 36's cost math)"
    }
    if ($otelConfig -match "keep-all-errors") {
        Check-Pass "tail_sampling includes explicit 'keep-all-errors' policy — no error trace loss"
    } elseif ($otelConfig -match "tail_sampling") {
        Check-Fail "tail_sampling present but no error-exemption policy found — verify errors aren't being sampled away"
    }
}

if (Test-Path "src/OrderService/BusinessMetrics.cs") {
    Check-Pass "BusinessMetrics.cs present — revenue/order-count instrumentation exists"
} else {
    Check-Warn "No BusinessMetrics.cs — revenue and billing queries will return zero rows, not errors (see Post 35's observability gap)"
}

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 6: INFRASTRUCTURE-AS-CODE HYGIENE
# ══════════════════════════════════════════════════════════════════════════
Write-Category "6. Infrastructure-as-Code Hygiene"

if (Test-Path ".github/workflows/ci-cd.yml") {
    $cicd = Get-Content ".github/workflows/ci-cd.yml" -Raw
    if ($cicd -match "cancel-in-progress:\s*true") {
        Check-Pass "concurrency.cancel-in-progress: true — prevents deployment races"
    } else {
        Check-Fail "No cancel-in-progress — concurrent deployments can race and deploy an older commit last"
    }
    if ($cicd -match "environment:\s*production") {
        Check-Pass "deploy job references 'environment: production' — required-reviewer gate available"
    } else {
        Check-Warn "No environment: production reference — confirm a manual approval gate exists before prod deploys"
    }
    if ($cicd -match '\$\{\{\s*github\.sha\s*\}\}') {
        Check-Pass "Image tags use github.sha — immutable, rollback-safe (not 'latest')"
    } else {
        Check-Fail "Image tags do not reference github.sha — rollback will not be deterministic"
    }
}

if (Test-Path ".github/workflows/pr-checks.yml") {
    $prChecks = Get-Content ".github/workflows/pr-checks.yml" -Raw
    if ($prChecks -match "az bicep build") {
        Check-Pass "PR checks validate Bicep syntax before merge"
    } else {
        Check-Warn "PR checks do not validate Bicep — infrastructure errors may reach main undetected"
    }
}

# ══════════════════════════════════════════════════════════════════════════
# CATEGORY 7: SCHEMA EVOLUTION SAFETY
# ══════════════════════════════════════════════════════════════════════════
Write-Category "7. Schema Evolution Safety"

if (Test-Path "src/Contracts/OrderCreatedEvent.cs") {
    $eventContract = Get-Content "src/Contracts/OrderCreatedEvent.cs" -Raw
    # POST 42: Any 'required' field ADDED after the initial contract design
    # is a red flag. This is a heuristic check — it can't know which fields
    # were in v1 vs added later, so it warns rather than fails.
    if ($eventContract -match "public\s+OrderPriority\?\s+Priority") {
        Check-Pass "OrderPriority field is nullable — forward/backward compatible (Post 38)"
    } else {
        Check-Warn "OrderPriority field not found as nullable — verify schema evolution safety if present"
    }
}

if (Test-Path "src/Contracts/OrderCreatedEventJsonContext.cs") {
    $jsonCtx = Get-Content "src/Contracts/OrderCreatedEventJsonContext.cs" -Raw
    if ($jsonCtx -match "WhenWritingNull") {
        Check-Pass "JsonIgnoreCondition.WhenWritingNull configured — null fields omitted from wire format"
    } else {
        Check-Warn "No WhenWritingNull — null fields will appear explicitly in wire format, increasing payload size and old-consumer risk"
    }
    if ($jsonCtx -match "JsonStringEnumConverter") {
        Check-Pass "JsonStringEnumConverter configured — enum values serialise as strings, safe for future extension"
    } else {
        Check-Warn "No JsonStringEnumConverter — integer enum serialisation risks breaking old consumers when new members are added"
    }
}

# ══════════════════════════════════════════════════════════════════════════
# SUMMARY
# ══════════════════════════════════════════════════════════════════════════
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  CHECKLIST SUMMARY                               ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host "  Passed:   $pass" -ForegroundColor Green
Write-Host "  Warnings: $warn" -ForegroundColor Yellow
Write-Host "  Failed:   $fail" -ForegroundColor $(if($fail -gt 0){"Red"}else{"Green"})

$effectiveFail = $fail + $(if ($Strict) { $warn } else { 0 })

if ($effectiveFail -eq 0) {
    Write-Host "`n  ✓ READY FOR PRODUCTION DEPLOYMENT" -ForegroundColor Green
    if ($warn -gt 0 -and -not $Strict) {
        Write-Host "  ($warn warning(s) present — review before shipping, re-run with -Strict to gate on them)" -ForegroundColor Yellow
    }
} else {
    Write-Host "`n  ✗ NOT READY — $effectiveFail blocking issue(s) found:" -ForegroundColor Red
    $findings | Where-Object { $_ -match "^FAIL" } | ForEach-Object { Write-Host "    $_" -ForegroundColor Red }
    if ($Strict) {
        $findings | Where-Object { $_ -match "^WARN" } | ForEach-Object { Write-Host "    $_" -ForegroundColor Yellow }
    }
    exit 1
}
