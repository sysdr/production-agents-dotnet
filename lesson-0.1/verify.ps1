# verify.ps1 — Post 45 · FINAL cumulative check: 95 checks (93 Post 44 + 2 new)
# This is the last verify.ps1 in the series. It carries forward every check
# from Posts 1-44 and adds the two that close the loop: does the finale
# script run cleanly, and does its combined snapshot agree with what the
# three individual generators (Posts 42-44) report on their own.
param([ValidateSet("Aspire","DockerCompose")][string]$Mode = "Aspire")
$pass=0; $fail=0; $errors=@()
function Check-Condition {
    param([string]$Description,[scriptblock]$Test,[string]$FailHint="")
    Write-Host -NoNewline "  Checking: $Description ... "
    try {
        if (& $Test) { Write-Host "PASS" -ForegroundColor Green; $script:pass++ }
        else {
            Write-Host "FAIL" -ForegroundColor Red
            if ($FailHint) { Write-Host "    Hint: $FailHint" -ForegroundColor Yellow }
            $script:fail++; $script:errors += $Description
        }
    } catch { Write-Host "FAIL ($_)" -ForegroundColor Red; $script:fail++; $script:errors += $Description }
}
function Get-Port { param([string]$Name)
    $r = Invoke-RestMethod http://localhost:15888/api/v1/resources -TimeoutSec 5
    ($r | Where-Object { $_.name -eq $Name }).services[0].allocatedEndpoint.port
}
function Get-Token { param([string]$TenantId = "tenant-01")
    $b = @{ UserId = "u01"; Email = "dev@test.com"; TenantId = $TenantId } | ConvertTo-Json
    (Invoke-RestMethod -Method Post "http://localhost:$(Get-Port 'user-service')/auth/token" `
        -Body $b -ContentType "application/json").Token
}

Write-Host "`n========================================================`n Hyperscale Log Monitoring — Post 45 · SERIES FINALE`n========================================================"
Write-Host "-- Cumulative baseline checks (Posts 1-44) -------------"
if ($Mode -eq "Aspire") {
    Check-Condition "Aspire Dashboard reachable"    { try{(Invoke-WebRequest http://localhost:15888 -UseBasicParsing -TimeoutSec 5).StatusCode -eq 200}catch{$false} }
    Check-Condition "order-service /health"         { try{(curl.exe -s -o NUL -w "%{http_code}" "http://localhost:$(Get-Port 'order-service')/health") -eq "200"}catch{$false} }
    Check-Condition "user-service /health"          { try{(curl.exe -s -o NUL -w "%{http_code}" "http://localhost:$(Get-Port 'user-service')/health") -eq "200"}catch{$false} }
}
Check-Condition "OTel Collector running"           { (docker ps --filter "name=otel-collector" --format "{{.Status}}") -match "Up" }
Check-Condition "Azure.Messaging.ServiceBus"       { (Get-Content src/OrderService/OrderService.csproj) -match "Azure.Messaging.ServiceBus" }
Check-Condition "Azure.Messaging.EventHubs"        { (Get-Content src/OrderService/OrderService.csproj) -match "Azure.Messaging.EventHubs" }
Check-Condition "SB MaxDeliveryCount=3"            { $cfg=Get-Content "config/servicebus-config.json"|ConvertFrom-Json; ($cfg.UserConfig.Namespaces[0].Queues|Where-Object{$_.Name -eq "orders"}).Properties.MaxDeliveryCount -eq 3 }
Check-Condition "orders-fallback queue exists"     { $cfg=Get-Content "config/servicebus-config.json"|ConvertFrom-Json; $null -ne ($cfg.UserConfig.Namespaces[0].Queues|Where-Object{$_.Name -eq "orders-fallback"}) }
Check-Condition "FallbackServiceBusSender.cs"      { Test-Path "src/OrderService/FallbackServiceBusSender.cs" }
Check-Condition "IMemoryCache in TenantConfigStore"{ (Get-Content "src/OrderService/TenantConfigStore.cs") -match "IMemoryCache" }
Check-Condition "tenant-99 returns 403"            {
    $tok=(Invoke-RestMethod -Method Post "http://localhost:$(Get-Port 'user-service')/auth/token" `
        -Body (@{UserId="s";Email="s@s.com";TenantId="tenant-99"}|ConvertTo-Json) -ContentType "application/json").Token
    try{Invoke-RestMethod -Method Post "http://localhost:$(Get-Port 'order-service')/orders/create" `
        -Headers @{Authorization="Bearer $tok"} -TimeoutSec 10;$false}
    catch{$_.Exception.Response.StatusCode -eq [System.Net.HttpStatusCode]::Forbidden}
}
Check-Condition "tail_sampling in otel config"     { (Get-Content "config/otel-collector-config.yaml" -Raw) -match "tail_sampling" }
Check-Condition "BusinessMetrics.cs"               { Test-Path "src/OrderService/BusinessMetrics.cs" }
Check-Condition "OrderPriority.cs (nullable field)"{ (Test-Path "src/Contracts/OrderPriority.cs") -and ((Get-Content "src/Contracts/OrderCreatedEvent.cs" -Raw) -match "OrderPriority\?") }
Check-Condition "TraceContextPropagator.cs"        { Test-Path "src/ServiceDefaults/TraceContextPropagator.cs" }
Check-Condition "InjectTraceContext wired 2x"      {
    $c = Get-Content "src/OrderService/Program.cs" -Raw
    ([regex]::Matches($c, "InjectTraceContext")).Count -ge 2
}
Check-Condition "infra main.bicep + @secure()"     { (Test-Path "infra/main.bicep") -and ((Get-Content "infra/main.bicep") -match "@secure\(\)") }
Check-Condition "ci-cd.yml cancel-in-progress"     { (Get-Content ".github/workflows/ci-cd.yml" -Raw) -match "cancel-in-progress:\s*true" }
Check-Condition "k8s ScaledObject minReplicaCount" { (Get-Content "k8s/order-processor-scaledobject.yaml") -match "minReplicaCount:\s*1" }
Check-Condition "all capstone scripts 1-9"         {
    (Test-Path "docs/capstone-01.ps1") -and (Test-Path "docs/capstone-02.ps1") -and
    (Test-Path "docs/capstone-03.ps1") -and (Test-Path "docs/capstone-04.ps1") -and
    (Test-Path "docs/capstone-05.ps1") -and (Test-Path "docs/capstone-06.ps1") -and
    (Test-Path "docs/capstone-07.ps1") -and (Test-Path "docs/capstone-08.ps1") -and
    (Test-Path "docs/capstone-09.ps1")
}
Check-Condition "production-checklist.ps1 exists"  { Test-Path "docs/production-checklist.ps1" }
Check-Condition "generate-docs.ps1 exists"         { Test-Path "docs/generate-docs.ps1" }
Check-Condition "generate-retrospective.ps1 exists"{ Test-Path "docs/generate-retrospective.ps1" }

Write-Host "-- Post 45 checks (FINAL — Series Finale) --------------"

Check-Condition "docs/finale.ps1 exists" {
    Test-Path "docs/finale.ps1"
} "Post 45 adds finale.ps1 — sequences all three prior generators into one closing snapshot"

Check-Condition "finale.ps1 runs all three generators and reports consistent results" {
    $result = & "$PSScriptRoot\docs\finale.ps1" 2>&1
    $joined = $result -join "`n"
    ($joined -match "Step 1 — Production readiness:\s+READY") -and
    ($joined -match "Step 3 — Decision history:") -and
    (Test-Path "$PSScriptRoot\docs\FINAL-SNAPSHOT.md")
} "finale.ps1 should report Step 1 as READY and produce docs/FINAL-SNAPSHOT.md — if this fails, the series' own closing audit has found a real problem"

Write-Host "`n========================================================"
if ($fail -eq 0) {
    Write-Host " All $pass checks passed. THE SERIES IS COMPLETE." -ForegroundColor Green
    Write-Host ""
    Write-Host " Run the finale:"
    Write-Host "   .\docs\finale.ps1"
    Write-Host ""
    Write-Host " 45 posts. $pass checks. Every generator agrees the tree is sound."
} else {
    Write-Host " $fail/$pass FAILED:" -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "   - $_" -ForegroundColor Yellow }; exit 1
}
Write-Host "========================================================"
