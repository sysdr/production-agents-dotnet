# Capstone 02 — Tenant Isolation Failure: Security Incident Playbook
# Distributed Systems with .NET — hyperscale-log-monitoring · Post 34
#
# ── WHAT TENANT ISOLATION MEANS IN THIS SYSTEM ───────────────────────────
#
# Every HTTP request to /orders/create must carry a JWT token with a TenantId claim.
# TenantAuthorizationHandler (Post 21) checks that the TenantId exists AND IsEnabled=true
# in the tenantconfig Table Storage table.
# If either check fails → 403 Forbidden. No order created. No Service Bus message.
#
# The invariant: a request authenticated for tenant-A can NEVER create data
# attributed to tenant-B, and an unknown tenant (not in Table Storage) can
# never create data at all.
#
# ── THE REALISTIC FAILURE MODES ──────────────────────────────────────────
#
# Failure Mode 1: Missing null check (code defect)
#
#   // VULNERABLE — context.Succeed() called even when config is null
#   var config = await _tenantConfigStore.GetTenantConfigAsync(tenantId);
#   if (config == null || !config.IsEnabled)
#   {
#       // Bug: forgot this block entirely, or wrote context.Succeed() by mistake
#       context.Succeed(requirement); return;
#   }
#
#   // CORRECT
#   var config = await _tenantConfigStore.GetTenantConfigAsync(tenantId);
#   if (config == null || !config.IsEnabled)
#   {
#       context.Fail();
#       return;
#   }
#   context.Succeed(requirement);
#
# Failure Mode 2: Cache serves stale enabled-state (race condition)
#
#   Sequence:
#     T+0s   Admin sets IsEnabled=false for tenant-01 (in Table Storage)
#     T+0s   TenantConfigStore cache still holds IsEnabled=true (30s TTL from Post 28)
#     T+30s  Cache expires, next request reads IsEnabled=false from Table Storage
#     T+30s  tenant-01 now correctly blocked
#
#   The 30-second window where a disabled tenant can still create orders is
#   the TTL trade-off documented in Post 28. Accept it, or add a pub/sub
#   cache invalidation channel (Service Bus → OrderService → evict cache).
#
# Failure Mode 3: Token forging (incorrect signing key validation)
#
#   If ValidateIssuerSigningKey=false or the signing key leaks, an attacker
#   can forge a JWT with TenantId="tenant-01" even without credentials.
#   Detection: the JwtBearerEvents telemetry (Post 20) shows an unusual spike
#   in successful validations from a new IP range.
#
# ── DETECTION SIGNALS IN TELEMETRY ───────────────────────────────────────
#
# Signal 1: OTel span TenantId attribute on successful order spans
#   Every successful POST /orders/create span carries TenantId in Attributes.
#   An unknown TenantId on a successful span is definitive evidence of breach.
#
# Signal 2: Service Bus message TenantId property
#   OrderCreatedEvent carries TenantId (Post 13).
#   A message with an unknown TenantId in Service Bus means an order was created.
#
# Signal 3: Blob Storage path contains TenantId
#   processed-orders/{year}/{month}/{tenantId}/{orderId}.json (Post 16)
#   Finding files under an unknown tenantId path is forensic evidence.
#
# Signal 4: Table Storage absence
#   If a TenantId appears in spans but has no row in tenantconfig table:
#   the isolation control was bypassed.
#
# ── REMEDIATION DECISION TREE ────────────────────────────────────────────
#
# STEP 1: Quantify the breach
#   - How many orders were created for unknown tenants?
#   - Over what time window?
#   - Run ADX Query 2 from capstone-02.ps1
#
# STEP 2: Contain
#   - If code defect: emergency deploy the fix via GitHub Actions (est. 10 min)
#   - If cache race: no immediate action needed — race window is max 30s
#   - If token forging: rotate the JWT signing key immediately
#     (requires updating Jwt__SigningKey secret + redeploy)
#
# STEP 3: Investigate the breached orders
#   - Retrieve affected orders from Blob Storage
#     (path: processed-orders/*/unknown-tenant-id/*.json)
#   - Determine if PII or payment data was exposed
#   - If yes: escalate to legal/compliance
#
# STEP 4: Notify (if required)
#   - Check your jurisdiction's breach notification requirements
#   - Azure data residency boundaries (East US vs EU)
#
# STEP 5: Post-mortem
#   - Document timeline using ADX forensics queries
#   - Add a Testcontainers integration test (Post 26) that asserts 403
#     for unknown tenants — this test would have caught the code defect
#
# ── THE INTEGRATION TEST THAT PREVENTS THIS ──────────────────────────────
#
# Add to tests/IntegrationTests/TenantAuthorizationTests.cs:
#
#   [Fact]
#   public async Task UnknownTenant_Returns403()
#   {
#       // Arrange: token with a tenant ID not in Table Storage
#       var token = await GetToken("tenant-unknown-xyz");
#       _client.DefaultRequestHeaders.Authorization =
#           new AuthenticationHeaderValue("Bearer", token);
#
#       // Act
#       var response = await _client.PostAsync("/orders/create",
#           new StringContent("{}", Encoding.UTF8, "application/json"));
#
#       // Assert: isolation MUST return 403
#       Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
#   }
#
# This test runs in CI/CD (pr-checks.yml, Post 30) and blocks any PR
# that accidentally removes the isolation check.
