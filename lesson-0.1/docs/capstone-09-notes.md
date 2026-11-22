# Capstone 09 — The Complete Incident Lifecycle: Reference
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 41
#
# ── WHY THIS CAPSTONE REUSES SCRIPTS INSTEAD OF REWRITING THEM ───────────
#
# capstone-09.ps1 deliberately calls into capstone-01.ps1 through
# capstone-08.ps1 rather than reimplementing their logic. This mirrors
# how a real on-call engineer actually works during an incident: you reach
# for the runbook and script you already have, you don't write bespoke
# code under pressure. If capstone-05.ps1 (Service Bus failover) has a bug,
# fixing it in ONE place fixes it for both the standalone Post 37 capstone
# AND this finale — there is only one source of truth for each mechanism.
#
# ── THE NINE-SCENE STRUCTURE ──────────────────────────────────────────────
#
# Scenes 1-8 map directly to Capstones 01-08 (Posts 33-40), run in the
# SAME order they were originally taught — the pedagogical sequence and
# the narrative sequence are intentionally identical. Scene 9 is new: it
# synthesizes all eight scenes into a single ADX post-mortem query that
# no individual capstone could produce alone, because it needs the full
# shift's time window to be meaningful.
#
# The connective narration between scenes is not decorative — each
# "Write-Connective" call explains WHY the previous scene's resolution
# creates the conditions for the next scene. This is intentional: real
# incidents are causally connected, not independent unit tests. A burst
# (Scene 1) is exactly the condition under which a security scanner is
# most likely to also be probing (Scene 2, real attackers/scanners often
# increase activity during visible incidents), and burst-generated span
# volume is exactly what creates ADX cost pressure (Scene 4).
#
# ── QUICKMODE vS FULL MODE ────────────────────────────────────────────────
#
# Full mode runs each underlying capstone with its DEFAULT parameters —
# this takes 15-25 minutes total (Capstone 01 alone is ~2 minutes at
# default load, Capstone 08 is ~90 seconds of compound chaos, etc.)
#
# QuickMode reduces load/duration parameters across every scene while
# preserving the NARRATIVE STRUCTURE — same nine scenes, same causal
# ordering, same final reconciliation query — just faster. Use QuickMode
# for demos or CI smoke-testing of the finale script itself; use full
# mode when you want representative telemetry volume for the Scene 9
# post-mortem query to be meaningful.
#
# ── THE SCENE 9 POST-MORTEM QUERY ─────────────────────────────────────────
#
# This is the query this entire 41-post series has been building toward.
# It uses KQL's `union` operator to combine two structurally different
# result sets (a latency percentile time series from OTelTraces, and a
# resilience-event count time series from OTelLogs) into ONE chronological
# view of the shift. This is exactly the query a senior SRE opens in a
# post-mortem meeting: "here is the latency curve for the incident window,
# and here is every resilience mechanism that fired, overlaid on the same
# timeline." No manual correlation across four separate dashboards.
#
# ── WHAT "SHIP READINESS" LOOKS LIKE AFTER THIS CAPSTONE ─────────────────
#
# By the end of Scene 8 (compound chaos, Post 40's mechanism), a fix
# identified in Scene 7 (trace correlation, Post 39) has been validated
# not just in isolation but under the SAME combination of stressors that
# caused the original incident. This is a meaningfully higher bar than
# "the fix passed its unit test" — it's "the fix passed under conditions
# that reproduce the actual failure mode," which is the standard senior
# engineers are held to before an incident is considered truly resolved.
#
# ── RUNNING THIS AGAINST A REAL PRODUCTION SYSTEM (CONCEPTUALLY) ─────────
#
# This capstone runs entirely against the local Aspire emulator stack.
# The PATTERN it demonstrates — chain your existing incident-response
# scripts into a rehearsed sequence, with a synthesizing post-mortem query
# at the end — is exactly what a mature SRE team builds as a "game day"
# exercise: scheduled, rehearsed incident simulations using the SAME
# tooling that would be used in a real incident, not synthetic tabletop
# exercises with no connection to actual system behavior.
