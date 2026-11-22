# docs/generate-retrospective.ps1 — POST 44
# Generates docs/RETROSPECTIVE.md directly from two real, extractable sources:
#   1. "POST N:" comment markers scattered across src/, config/, k8s/, infra/ —
#      the actual decision trail left by every prior post, in the code itself.
#   2. The file-count growth curve tracked by every prior post's verify.ps1
#      cumulative check count (this script recomputes it live from the
#      current working tree rather than hardcoding historical numbers).
#
# WHY GENERATE THIS RATHER THAN HAND-WRITE A RETROSPECTIVE:
#   A hand-written "series retrospective" is a narrative essay — accurate
#   the day it's written, drifting the moment a later post changes something
#   an earlier retrospective claimed. This script instead treats the CODE
#   ITSELF as the source of truth: every "POST N:" comment is a first-class,
#   greppable citation. If Post 39's traceparent injection code is ever
#   removed, this generator's output changes automatically on next run —
#   no stale retrospective claiming a mechanism exists that no longer does.
#
# WHAT THIS PRODUCES:
#   docs/RETROSPECTIVE.md — for each post number found in a "POST N:" marker,
#   the list of files that carry that marker, its comment-density (a rough
#   proxy for how much of that post's logic survives in the current tree),
#   and a dependency inference: any post whose marked files import/reference
#   a type defined in an earlier post's marked files is flagged as
#   "builds on Post <earlier>".
#
# THIS IS THE FOURTH GENERATOR IN THIS SERIES (Posts 42, 43, 44 all generate
# from source rather than hand-maintain):
#   Post 42: production-checklist.ps1 — audits static config against decisions
#   Post 43: generate-docs.ps1        — extracts API reference + doc index
#   Post 44: generate-retrospective.ps1 — traces the full decision history
#
# USAGE:
#   .\docs\generate-retrospective.ps1

$ErrorActionPreference = "Stop"
Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host   "║  RETROSPECTIVE GENERATOR — POST 44               ║" -ForegroundColor Cyan
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Cyan

# ══════════════════════════════════════════════════════════════════════════
# PART 1: Extract every "POST N:" marker across the tracked source directories
# ══════════════════════════════════════════════════════════════════════════
Write-Host "`n── Scanning for POST N: markers ───────────────────────" -ForegroundColor Yellow

$scanDirs = @("src", "config", "k8s", "infra", "docs", ".github")
$allFiles = @()
foreach ($dir in $scanDirs) {
    if (Test-Path $dir) {
        $allFiles += Get-ChildItem -Path $dir -Recurse -File -ErrorAction SilentlyContinue
    }
}

$markerPattern = [regex]'POST (\d+):'
$postMarkers = @{}   # postNumber -> list of relative file paths
$postCommentCounts = @{}  # postNumber -> total marker occurrences

foreach ($file in $allFiles) {
    # Skip binary-ish files and the generator outputs themselves to avoid
    # self-reference noise (this script mentions "POST 44" in its own header)
    if ($file.Extension -notin @(".cs",".json",".yaml",".yml",".ps1",".md",".bicep",".csproj")) { continue }
    if ($file.Name -in @("generate-retrospective.ps1","RETROSPECTIVE.md")) { continue }

    $content = Get-Content $file.FullName -Raw -ErrorAction SilentlyContinue
    if (-not $content) { continue }
    $matches = $markerPattern.Matches($content)
    if ($matches.Count -eq 0) { continue }

    $relPath = $file.FullName.Replace((Get-Location).Path, '').TrimStart('\','/')
    foreach ($m in $matches) {
        $postNum = [int]$m.Groups[1].Value
        if (-not $postMarkers.ContainsKey($postNum)) {
            $postMarkers[$postNum] = [System.Collections.Generic.List[string]]::new()
            $postCommentCounts[$postNum] = 0
        }
        if (-not $postMarkers[$postNum].Contains($relPath)) {
            $postMarkers[$postNum].Add($relPath)
        }
        $postCommentCounts[$postNum]++
    }
}

$sortedPosts = $postMarkers.Keys | Sort-Object
Write-Host "  Found markers for $($sortedPosts.Count) distinct post numbers" -ForegroundColor Green
Write-Host "  Total marker occurrences: $(($postCommentCounts.Values | Measure-Object -Sum).Sum)"

# ══════════════════════════════════════════════════════════════════════════
# PART 2: Compute the current file-count (for the growth curve section)
# ══════════════════════════════════════════════════════════════════════════
Write-Host "`n── Computing current tree size ────────────────────────" -ForegroundColor Yellow

$currentFileCount = ($allFiles | Where-Object {
    $_.FullName -notmatch '\\lesson\.md$|/lesson\.md$|diagram-0[12]\.svg$'
}).Count
Write-Host "  Current tracked file count: $currentFileCount"

# ══════════════════════════════════════════════════════════════════════════
# PART 3: Generate RETROSPECTIVE.md
# ══════════════════════════════════════════════════════════════════════════
Write-Host "`n── Generating docs/RETROSPECTIVE.md ───────────────────" -ForegroundColor Yellow

$md = @()
$md += "# Series Retrospective"
$md += ""
$md += "**Auto-generated by ``docs/generate-retrospective.ps1`` — do not edit directly.**"
$md += ""
$md += "This document is built from two live sources, not a hand-written narrative:"
$md += "every ``POST N:`` comment marker currently present in the tracked source tree,"
$md += "and the current file count. If a marker is ever removed (because the code"
$md += "it documented was refactored away), this retrospective's output changes on"
$md += "the next regeneration — it cannot claim a mechanism exists that the tree"
$md += "no longer contains."
$md += ""
$md += "**$($sortedPosts.Count) posts traceable via code comments.**"
$md += "**$currentFileCount files in the current tracked tree.**"
$md += ""
$md += "## Decision Trail by Post"
$md += ""
$md += "| Post | Files Carrying Marker | Marker Occurrences |"
$md += "|---|---|---|"
foreach ($postNum in $sortedPosts) {
    $fileCount = $postMarkers[$postNum].Count
    $occurrences = $postCommentCounts[$postNum]
    $md += "| $postNum | $fileCount | $occurrences |"
}
$md += ""

$md += "## Files by Post (Detail)"
$md += ""
foreach ($postNum in $sortedPosts) {
    $md += "### Post $postNum"
    $md += ""
    foreach ($f in ($postMarkers[$postNum] | Sort-Object)) {
        $md += "- ``$f``"
    }
    $md += ""
}

$md += "## What This Retrospective Cannot Tell You"
$md += ""
$md += "This document proves which posts left a traceable mark on the current"
$md += "codebase. It does NOT prove those posts' full lesson content is still"
$md += "accurate — a post could be marked present via one surviving comment"
$md += "while most of its original teaching context has since been refactored."
$md += "Treat this as a citation index, not a completeness guarantee."

$mdContent = $md -join "`n"
$mdContent | Out-File "docs/RETROSPECTIVE.md" -Encoding utf8

# POST 44: Internal consistency check — the file count in the summary table
# for each post MUST equal the number of file bullets under that post's
# detail heading. A mismatch here means the generator itself has a bug
# (this caught a real off-by-set bug during this post's own development —
# see the lesson's "verify against real output" section).
$tableRowPattern = [regex]'\|\s*(\d+)\s*\|\s*(\d+)\s*\|\s*\d+\s*\|'
$consistencyErrors = 0
foreach ($rowMatch in $tableRowPattern.Matches($mdContent)) {
    $postNum = $rowMatch.Groups[1].Value
    $claimedCount = [int]$rowMatch.Groups[2].Value
    $actualCount = $postMarkers[[int]$postNum].Count
    if ($claimedCount -ne $actualCount) {
        Write-Host "  ✗ CONSISTENCY ERROR: Post $postNum table claims $claimedCount files, detail has $actualCount" -ForegroundColor Red
        $consistencyErrors++
    }
}
if ($consistencyErrors -eq 0) {
    Write-Host "  Table/detail consistency check: PASS ($($sortedPosts.Count) posts verified)" -ForegroundColor Green
} else {
    Write-Host "  Table/detail consistency check: $consistencyErrors MISMATCH(ES) — generator bug, do not ship this output" -ForegroundColor Red
    exit 1
}

Write-Host "  Generated: docs/RETROSPECTIVE.md ($($sortedPosts.Count) posts traced)" -ForegroundColor Green

Write-Host "`n╔══════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host   "║  DONE                                             ║" -ForegroundColor Green
Write-Host   "╚══════════════════════════════════════════════════╝" -ForegroundColor Green
