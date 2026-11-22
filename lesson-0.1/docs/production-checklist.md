# Production Deployment Checklist — Post 42
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# This document is the human-readable companion to production-checklist.ps1.
# Run the script for an automated pass/fail audit; read this for the
# reasoning behind each check and the specific post where the decision
# was made.
#
# ── 1. VERSION PINNING INTEGRITY ─────────────────────────────────────────
#
# □ Every <PackageReference> across all .csproj files has an explicit
#   Version attribute — no floating "*" or missing versions.
#   Why: an unpinned package can silently upgrade between a local build
#   and a CI build, producing a "works on my machine" deployment failure.
#
# □ No Docker image in docker-compose.yml or any Dockerfile uses the
#   ':latest' tag.
#   Why: 'latest' is mutable — the same tag can point to a different image
#   tomorrow. Post 30's SHA-tagging principle for your OWN build output
#   extends to base images too.
#
# □ The pinned version table (23 packages + 1 Docker image, see project
#   memory) matches what's actually in every .csproj file. Any drift
#   between "what we say we pin" and "what's actually referenced" is a
#   documentation bug waiting to become a production incident.
#
# ── 2. SECURITY POSTURE ───────────────────────────────────────────────────
#
# □ infra/main.bicep's jwtSigningKey parameter carries @secure() —
#   Post 29. Without it, the signing key is stored in plain text in
#   ARM deployment history, retrievable by anyone with Reader access
#   to the resource group.
#
# □ TenantAuthorizationHandler (in TenantAuthorizationRequirement.cs)
#   explicitly calls context.Fail() on every negative path — Post 34.
#   A handler that falls through without calling Fail() or Succeed()
#   does not explicitly deny the request; other handlers in the policy
#   chain may still allow it through. This is the exact vulnerability
#   Capstone 02 was built to detect.
#
# □ The 403-for-unknown-tenant behaviour is covered by an automated test
#   (tests/IntegrationTests) that runs in CI on every PR — not just
#   verified manually. A test that only exists as a runbook step gets
#   skipped under deadline pressure; a test that runs in CI cannot be.
#
# ── 3. RESILIENCE CONFIGURATION ───────────────────────────────────────────
#
# □ Both 'orders' and 'orders-fallback' Service Bus queues exist with
#   matching MaxDeliveryCount (3) — Post 9, Post 37. Mismatched DLQ
#   behaviour between primary and fallback queues means an order that
#   fails processing behaves differently depending on which queue
#   Polly happened to route it through during an outage.
#
# □ FallbackServiceBusSender.cs is present and wired into every publish
#   path (both Service Bus and, if applicable, Event Hubs) — Post 37,
#   Post 39. A partial wiring (fallback on one transport but not another)
#   means half your outage protection doesn't exist.
#
# □ Polly retry counts and timeout values in OrderProcessorWorker.cs
#   match the documented SLA — 3 retries, exponential backoff — Post 9/10.
#
# ── 4. SCALING CONFIGURATION ──────────────────────────────────────────────
#
# □ k8s/order-processor-scaledobject.yaml: minReplicaCount is 1, not 0 —
#   Post 31. Confirm this is a deliberate choice: minReplicaCount: 0 saves
#   cost during idle periods but introduces 20-70 second cold-start latency
#   on the first message after idle (documented cost/latency trade-off).
#
# □ cooldownPeriod is at least 180 seconds (2x the typical inter-burst gap
#   for your traffic pattern) — Post 31. Below this, KEDA may thrash
#   between scale-up and scale-down during moderate traffic variance.
#
# □ maxReplicaCount in the ScaledObject matches maxReplicas in
#   infra/main.bicep's Container App module — Post 29 vs Post 31. A
#   mismatch here means your AKS and Container Apps deployment paths
#   (if you maintain both) have different effective scaling ceilings.
#
# ── 5. OBSERVABILITY COMPLETENESS ─────────────────────────────────────────
#
# □ ADX dual-export (azuredataexplorer exporter) is configured — Post 32.
#   Without it, only short-retention telemetry (App Insights default 90
#   days) exists — no long-window KQL analytics for capacity planning
#   or compliance reporting.
#
# □ tail_sampling is active with an explicit "keep-all-errors" policy —
#   Post 36. Verify the policy list has NOT been reordered or edited such
#   that errors could fall through to the probabilistic sampling policy.
#
# □ BusinessMetrics.cs (or equivalent domain metrics) exists — Post 35.
#   Run `OTelMetrics | distinct MetricName` in ADX and compare against a
#   written list of what SHOULD be measured (revenue, order counts, any
#   metric a billing or product team will eventually ask for).
#
# ── 6. INFRASTRUCTURE-AS-CODE HYGIENE ─────────────────────────────────────
#
# □ ci-cd.yml has concurrency.cancel-in-progress: true — Post 30. Without
#   it, two rapid pushes to main can race and the OLDER commit can win,
#   silently reverting a fix.
#
# □ The deploy job references environment: production with a configured
#   required-reviewer gate in GitHub repo settings — Post 30. Verify this
#   in Settings → Environments, not just in the YAML — the YAML reference
#   alone does nothing without the environment protection rule configured.
#
# □ Image tags use ${{ github.sha }}, never a floating tag — Post 30.
#   Rollback via `az deployment group create --parameters imageTag=<SHA>`
#   only works if every historical deploy used a real SHA, not 'latest'.
#
# □ pr-checks.yml runs `az bicep build` on every PR — Post 29/30. Bicep
#   syntax errors should never reach main, let alone a production deploy.
#
# ── 7. SCHEMA EVOLUTION SAFETY ────────────────────────────────────────────
#
# □ Every new field added to a shared event contract since v1 is nullable
#   (or has a safe default) — Post 38. Search for `required` on any field
#   added after the initial OrderCreatedEvent design; a required field
#   added later is the exact DLQ-surge bug this series demonstrated.
#
# □ JsonIgnoreCondition.WhenWritingNull is configured on every JsonContext
#   for shared contracts — Post 38. Confirms null fields are omitted from
#   the wire format rather than serialized as explicit nulls.
#
# □ All enums in shared contracts use JsonStringEnumConverter, not the
#   default integer serialization — Post 38. Protects against a future
#   enum member addition breaking consumers still running an older build.
#
# ── SIGN-OFF ───────────────────────────────────────────────────────────────
#
# This checklist does not replace judgment — it's a floor, not a ceiling.
# A system can pass every check here and still have production issues
# specific to your traffic pattern, compliance requirements, or team's
# operational maturity. Use production-checklist.ps1 as the automated
# gate; use this document as the discussion guide for what each gate
# actually protects against, and why it was built the way it was.
