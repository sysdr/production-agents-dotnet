// infra/main.bicep — POST 29: Azure Bicep IaC for the hyperscale-log-monitoring system
//
// WHAT THIS DEPLOYS:
//   5 modules → Azure Container Apps (order-service, order-processor, order-event-processor,
//                                     user-service), Service Bus, Event Hubs, Azure Storage
//
// BRIDGE FROM FAMILIAR:
//   ARM JSON (azure-deploy.json) is the equivalent in raw Azure Resource Manager.
//   Bicep compiles to ARM JSON — every Bicep file can be verified with:
//     az bicep build --file infra/main.bicep
//   CloudFormation (AWS) and Terraform are conceptually equivalent; Bicep is
//   Azure-native and requires no state file — Azure Resource Manager is the state store.
//
// ZERO AZURE SUBSCRIPTION NEEDED TO VALIDATE:
//   az bicep build --file infra/main.bicep
//   Compiles and validates syntax without deploying. No subscription required.
//
// TO DEPLOY (requires Azure subscription + az login):
//   az group create -n rg-orders-dev -l eastus
//   az deployment group create \
//     --resource-group rg-orders-dev \
//     --template-file infra/main.bicep \
//     --parameters infra/parameters.bicepparam

// ── Parameters ────────────────────────────────────────────────────────────
@description('Environment name — determines resource name prefix and SKU tier.')
@allowed(['dev', 'staging', 'prod'])
param environmentName string = 'dev'

@description('Azure region for all resources. Defaults to the resource group region.')
param location string = resourceGroup().location

@description('Container image tag — set to Git SHA by the CI/CD pipeline (Post 30).')
param imageTag string = 'latest'

@description('Container registry FQDN — e.g. myregistry.azurecr.io')
param containerRegistryServer string

// POST 29: @secure() prevents ARM from logging the value in deployment history.
// This is the ONLY safe way to pass secrets into Bicep — never use a plain param.
// The value flows from parameters.bicepparam (local/dev) or GitHub Secrets (CI/CD).
@description('JWT signing key — must match Jwt:SigningKey in appsettings.')
@secure()
param jwtSigningKey string

// ── Deterministic naming ──────────────────────────────────────────────────
// POST 29: uniqueString(resourceGroup().id) produces a stable 13-char hash.
// Same resource group always produces the same suffix → idempotent re-deploys.
// Different resource groups (dev vs prod) → different suffixes → no naming collision.
// Storage account names must be globally unique, lowercase, 3-24 chars — uniqueString handles this.
var resourceSuffix  = uniqueString(resourceGroup().id)
var prefix          = '${environmentName}-orders'
var storageAcctName = toLower(replace('${prefix}${resourceSuffix}', '-', ''))

// ── Modules ───────────────────────────────────────────────────────────────

// POST 29: Each Azure resource type lives in its own module.
// This mirrors the Aspire manifest structure (Post 22): each named resource
// in Program.cs becomes one module here.
// Module outputs are used as inputs to later modules — Bicep resolves the
// dependency graph and deploys in the correct order automatically.

module serviceBus 'modules/service-bus.bicep' = {
  name: '${prefix}-sb-${resourceSuffix}'
  params: {
    namespaceName  : '${prefix}-sb-${resourceSuffix}'
    location       : location
    // POST 29: SKU is environment-aware. dev uses Basic (no Topics, no sessions).
    // Use Standard for staging/prod — Topics are required for Dapr pub/sub (Post 25).
    sku            : environmentName == 'dev' ? 'Basic' : 'Standard'
    queueName      : 'orders'
    maxDeliveryCount: 3   // Matches servicebus-config.json (Post 9)
  }
}

module storage 'modules/storage.bicep' = {
  name: '${prefix}-storage-${resourceSuffix}'
  params: {
    accountName     : storageAcctName
    location        : location
    // POST 29: LRS for dev (cheapest). GRS for prod (geo-redundant, required for SLA).
    replication     : environmentName == 'prod' ? 'GRS' : 'LRS'
    blobContainers  : ['processed-orders']   // Matches OrderBlobStore path (Post 16)
    tables          : ['tenantconfig']        // Matches TenantConfigStore (Post 17)
  }
}

module eventHubs 'modules/event-hubs.bicep' = {
  name: '${prefix}-eh-${resourceSuffix}'
  params: {
    namespaceName   : '${prefix}-eh-${resourceSuffix}'
    location        : location
    sku             : environmentName == 'prod' ? 'Standard' : 'Basic'
    eventHubName    : 'order-events'
    partitionCount  : 4     // Matches eventhubs-config.json (Post 12)
    consumerGroups  : ['order-processor', 'analytics']
  }
}

module containerAppsEnv 'modules/container-apps-env.bicep' = {
  name: '${prefix}-cae-${resourceSuffix}'
  params: {
    environmentName : '${prefix}-cae'
    location        : location
  }
}

// POST 29: Each service is a separate container-app module invocation.
// The same module handles all services — only params differ.
// This avoids copy-pasting Container App resource blocks, keeping DRY.

module orderService 'modules/container-app.bicep' = {
  name: '${prefix}-order-service-${resourceSuffix}'
  params: {
    appName         : 'order-service'
    location        : location
    environmentId   : containerAppsEnv.outputs.environmentId
    image           : '${containerRegistryServer}/order-service:${imageTag}'
    minReplicas     : 1
    maxReplicas     : 10    // KEDA will override this at runtime (Post 31)
    // POST 29: Secret references — the value is stored in Container Apps secret store.
    // The env var references the secret by name, not by value.
    // This means the signing key never appears in plain text in the resource definition.
    secrets         : [
      { name: 'jwt-signing-key';      value: jwtSigningKey }
      { name: 'sb-connection-string'; value: serviceBus.outputs.connectionString }
      { name: 'storage-conn-string';  value: storage.outputs.connectionString }
      { name: 'eh-connection-string'; value: eventHubs.outputs.connectionString }
    ]
    env             : [
      { name: 'Jwt__SigningKey';                  secretRef: 'jwt-signing-key' }
      { name: 'ServiceBus__ConnectionString';     secretRef: 'sb-connection-string' }
      { name: 'Storage__ConnectionString';        secretRef: 'storage-conn-string' }
      { name: 'ASPNETCORE_ENVIRONMENT';           value: environmentName }
    ]
  }
}

module orderProcessor 'modules/container-app.bicep' = {
  name: '${prefix}-order-processor-${resourceSuffix}'
  params: {
    appName         : 'order-processor'
    location        : location
    environmentId   : containerAppsEnv.outputs.environmentId
    image           : '${containerRegistryServer}/order-processor:${imageTag}'
    minReplicas     : 1
    maxReplicas     : 10
    secrets         : [
      { name: 'sb-connection-string'; value: serviceBus.outputs.connectionString }
      { name: 'storage-conn-string';  value: storage.outputs.connectionString }
    ]
    env             : [
      { name: 'ServiceBus__ConnectionString'; secretRef: 'sb-connection-string' }
      { name: 'Storage__ConnectionString';    secretRef: 'storage-conn-string' }
      { name: 'ASPNETCORE_ENVIRONMENT';       value: environmentName }
    ]
  }
}

module userService 'modules/container-app.bicep' = {
  name: '${prefix}-user-service-${resourceSuffix}'
  params: {
    appName         : 'user-service'
    location        : location
    environmentId   : containerAppsEnv.outputs.environmentId
    image           : '${containerRegistryServer}/user-service:${imageTag}'
    minReplicas     : 1
    maxReplicas     : 5
    secrets         : [
      { name: 'jwt-signing-key'; value: jwtSigningKey }
    ]
    env             : [
      { name: 'Jwt__SigningKey';        secretRef: 'jwt-signing-key' }
      { name: 'ASPNETCORE_ENVIRONMENT'; value: environmentName }
    ]
  }
}

// ── Outputs ───────────────────────────────────────────────────────────────
// POST 29: Outputs surface values needed by CI/CD (Post 30) or KEDA (Post 31).
// ARM deployment outputs are visible in the Azure Portal and via az CLI:
//   az deployment group show -g rg-orders-dev -n main --query properties.outputs

output orderServiceFqdn  string = orderService.outputs.fqdn
output userServiceFqdn   string = userService.outputs.fqdn
output serviceBusName    string = serviceBus.outputs.namespaceName
output storageAccountName string = storage.outputs.accountName
