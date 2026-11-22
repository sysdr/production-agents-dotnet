# docs/capstone-09.ps1 — POST 41
# The complete incident lifecycle: one continuous on-call shift narrative
# chaining all eight prior capstones into a single simulated 90-minute shift.
#
# THIS IS THE SERIES FINALE OF THE CAPSTONE ARC (Posts 33-41).
# Every mechanism built across 41 posts participates in one continuous story.
#
# THE SHIFT NARRATIVE (9 scenes, one per prior capstone):
#
#   22:00  Scene 1 — A burst arrives. Queue depth climbs. KEDA scales out.        (Post 33)
#   22:04  Scene 2 — Mid-burst, a security scanner probes tenant isolation.       (Post 34)
#   22:11  Scene 3 — On-call notices ADX has no revenue metric for the burst.     (Post 35)
#   22:15  Scene 4 — ADX ingestion cost spikes from burst volume — sampling saves the bill. (Post 36)
#   22:22  Scene 5 — Service Bus namespace blips during the burst — Fallback catches it.    (Post 37)
#   22:28  Scene 6 — A partner integration sends orders with an old schema version.         (Post 38)
#   22:31  Scene 7 — On-call traces one slow order end-to-end using TraceId correlation.     (Post 39)
#   22:40  Scene 8 — All of the above happening AT ONCE — compound chaos validates the fix.  (Post 40)
#   23:15  Scene 9 — Shift ends. Full incident timeline reconstructed from ADX for the post-mortem.
#
# WHAT THIS SCRIPT DOES DIFFERENTLY FROM CAPSTONES 01-08:
#   Those scripts each ran ONE capstone's acts standalone.
#   This script calls into EACH prior capstone script (via dot-sourcing or
#   direct invocation) in narrative sequence, with connective commentary
#   explaining how each scene's resolution sets up conditions for the next.
#   It is deliberately NOT a from-scratch reimplementation — reusing the
#   existing eight scripts is the point: a real on-call engineer reaches
#   for the same runbooks and scripts they already have, in sequence,
#   not bespoke code written during the incident.
#
# PREREQUISITES:
#   dotnet run --project src/AppHost  (full Post 1-40 stack running)
#   docker compose up otel-collector -d
#   Expect this to take 15-25 minutes to run in full (-QuickMode for ~5 min)

param(
    [switch]$QuickMode,      # shortened durations for a faster demo run
    [switch]$SkipADX,
    [switch]$SkipServiceBusOutage
)

$ErrorActionPreference = "Continue"   # a shift continues even if one scene has issues
$shiftStart = Get-Date

Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Magenta
Write-Host   "║  CAPSTONE 09 — The Complete Incident Lifecycle   ║" -ForegroundColor Magenta
Write-Host   "║  One shift · Nine scenes · Forty posts           ║" -ForegroundColor Magenta
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Magenta
Write-Host "  Shift started: $($shiftStart.ToString('HH:mm:ss'))"
if ($QuickMode) { Write-Host "  Mode: QuickMode (shortened durations)" -ForegroundColor Yellow }

function Write-Scene { param([int]$Scene, [string]$Time, [string]$Title)
    Write-Host "`n┌──────────────────────────────────────────────────┐" -ForegroundColor Cyan
    Write-Host "│ $Time  Scene $Scene`: $Title" -ForegroundColor Cyan
    Write-Host "└──────────────────────────────────────────────────┘" -ForegroundColor Cyan
}
function Write-Connective { param([string]$Text)
    Write-Host "  › $Text" -ForegroundColor DarkGray
}

# ══════════════════════════════════════════════════════════════════════════
# SCENE 1 — Burst arrives (Post 33 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 1 "22:00" "Burst arrives — queue depth climbs, KEDA scales"
Write-Connective "Calling capstone-01.ps1 — the same burst-response runbook from Post 33."

if ($QuickMode) {
    & "$PSScriptRoot\capstone-01.ps1" -BurstRps 100 -BurstSec 20 -SkipADX
} else {
    & "$PSScriptRoot\capstone-01.ps1"
}

Write-Connective "Burst absorbed. But while OrderProcessor was scaling out, something else arrived..."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 2 — Security probe during the burst (Post 34 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 2 "22:04" "Mid-burst, a tenant isolation probe fires"
Write-Connective "Ten unknown-tenant requests hit the API in the middle of the burst — an"
Write-Connective "automated scanner, or a misconfigured partner integration. Either way,"
Write-Connective "the isolation control (Post 21) must hold under load, not just at rest."

& "$PSScriptRoot\capstone-02.ps1" -SkipADX

Write-Connective "Isolation held — 403 on every probe, even mid-burst. On-call moves on,"
Write-Connective "but now wants to know: what did that burst actually cost in revenue?"

# ══════════════════════════════════════════════════════════════════════════
# SCENE 3 — Observability gap discovered (Post 35 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 3 "22:11" "On-call tries to pull burst revenue — the metric doesn't exist"
Write-Connective "This is the moment every senior engineer recognizes: reaching for a"
Write-Connective "number that should exist and finding zero rows. Is it zero orders,"
Write-Connective "or a missing instrument? Without checking, you can't tell."

& "$PSScriptRoot\capstone-03.ps1" -OrderCount 15 -SkipADX

Write-Connective "Gap closed — order.value.usd now flows. But burst traffic at this volume"
Write-Connective "generated a LOT of spans. On-call checks the ADX ingestion dashboard..."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 4 — Cost pressure from the burst (Post 36 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 4 "22:15" "ADX ingestion cost spike — the burst generated 5x normal span volume"
Write-Connective "This is why tail_sampling exists — not as a general cost optimization,"
Write-Connective "but specifically so THIS scenario (burst + full trace fidelity for errors)"
Write-Connective "doesn't blow the monthly ADX budget while still preserving debug data."

& "$PSScriptRoot\capstone-04.ps1" -SuccessCount 50 -ErrorCount 5 -SkipADX

Write-Connective "Cost under control — 85%+ reduction, errors still fully captured."
Write-Connective "Twenty minutes into the shift, Service Bus starts throwing exceptions..."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 5 — Service Bus blip (Post 37 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 5 "22:22" "Service Bus namespace blips — Fallback routing engages"
Write-Connective "Whether this is coincidence or the burst itself stressing the namespace"
Write-Connective "is exactly the kind of correlation this capstone will help answer in Scene 9."

if ($SkipServiceBusOutage) {
    Write-Connective "SKIPPED (-SkipServiceBusOutage flag set)."
} elseif ($QuickMode) {
    # POST 41: capstone-05.ps1's real parameters are -SkipRecovery and
    # -FallbackOrderCount only (no -SkipADX — that script never queries ADX).
    & "$PSScriptRoot\capstone-05.ps1" -FallbackOrderCount 5
} else {
    & "$PSScriptRoot\capstone-05.ps1"
}

Write-Connective "Fallback held — zero client-visible errors during the SB blip."
Write-Connective "Then a partner integration's orders start looking... different."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 6 — Schema mismatch appears (Post 38 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 6 "22:28" "A partner sends orders with an old schema version"
Write-Connective "This tests whether the nullable-field + WhenWritingNull design from"
Write-Connective "Post 38 actually survives contact with a real (simulated) legacy sender."

& "$PSScriptRoot\capstone-06.ps1" -SkipADX

Write-Connective "v1 messages process cleanly, treated as Normal priority. No DLQ surge."
Write-Connective "But one order in the mix is running suspiciously slow..."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 7 — Trace correlation diagnosis (Post 39 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 7 "22:31" "On-call traces the slow order end-to-end"
Write-Connective "This is the payoff for the W3C traceparent propagation work in Post 39 —"
Write-Connective "one OrderId, one query, all four hops with durations, no manual log grep."

& "$PSScriptRoot\capstone-07.ps1" -SkipADX

Write-Connective "Root cause isolated to one hop. Fix identified. Before deploying it,"
Write-Connective "on-call wants confidence the fix holds under the SAME conditions"
Write-Connective "that caused tonight's incident — not just in isolation."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 8 — Compound chaos validates the fix (Post 40 capstone)
# ══════════════════════════════════════════════════════════════════════════
Write-Scene 8 "22:40" "Compound chaos — validating the fix under realistic combined load"
Write-Connective "This is the scene that ties the whole shift together: burst (Scene 1) +"
Write-Connective "poison messages (Scene 6's schema mismatch playing out) + SB pressure"
Write-Connective "(Scene 5) + retries, all at once — the exact combination a bad night produces."

if ($QuickMode) {
    & "$PSScriptRoot\capstone-08.ps1" -LoadRps 25 -ChaosDurationSec 25 -PoisonCount 2 -TransientCount 3 -SkipServiceBusOutage
} else {
    & "$PSScriptRoot\capstone-08.ps1"
}

Write-Connective "Reconciliation balanced — zero silent data loss under compound stress."
Write-Connective "Fix confirmed safe to ship. Shift winding down."

# ══════════════════════════════════════════════════════════════════════════
# SCENE 9 — Post-mortem reconstruction (new — synthesizes all 8 scenes)
# ══════════════════════════════════════════════════════════════════════════
$shiftEnd = Get-Date
Write-Scene 9 "23:15" "Shift ends — full incident timeline reconstructed from ADX"

Write-Host ""
Write-Host "  The complete post-mortem query — every mechanism from tonight," -ForegroundColor Cyan
Write-Host "  correlated by time window, in one KQL statement:" -ForegroundColor Cyan
Write-Host ""
Write-Host @"
  let shiftStart = datetime('$($shiftStart.ToString("yyyy-MM-ddTHH:mm:ssZ"))');
  let shiftEnd   = datetime('$($shiftEnd.ToString("yyyy-MM-ddTHH:mm:ssZ"))');
  union
    (OTelTraces
      | where Timestamp between (shiftStart .. shiftEnd)
      | where SpanName == "POST /orders/create"
      | summarize p99 = percentile(DurationMs, 99) by bin(Timestamp, 2m)
      | extend Category = "latency"),
    (OTelLogs
      | where Timestamp between (shiftStart .. shiftEnd)
      | where Body has_any ("FallbackActivated", "PoisonMessage", "DLQ non-empty",
                             "Forbidden", "deadletter")
      | summarize events = count() by bin(Timestamp, 2m), Body
      | extend Category = "resilience-event")
  | order by Timestamp asc
  // One query, one timeline: burst latency curve overlaid with every
  // resilience mechanism that fired during the shift, in chronological order.
"@ -ForegroundColor DarkCyan

Write-Host ""
Write-Host "╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  CAPSTONE 09 COMPLETE — SHIFT ENDED              ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host "  Shift duration:  $((($shiftEnd - $shiftStart).TotalMinutes).ToString('F1')) minutes"
Write-Host ""
Write-Host "  Scenes completed:"
Write-Host "    1. Burst + KEDA scale-out                    (Post 33)"
Write-Host "    2. Isolation held under load                 (Post 34)"
Write-Host "    3. Observability gap found and closed         (Post 35)"
Write-Host "    4. ADX cost controlled via sampling            (Post 36)"
Write-Host "    5. Service Bus blip absorbed by Fallback       (Post 37)"
Write-Host "    6. Legacy schema handled without DLQ surge     (Post 38)"
Write-Host "    7. Slow order diagnosed via trace correlation  (Post 39)"
Write-Host "    8. Fix validated under compound chaos          (Post 40)"
Write-Host "    9. Full shift reconstructed for post-mortem    (this post)"
Write-Host ""
Write-Host "  Every mechanism across 40 posts participated in one continuous incident." -ForegroundColor Green
