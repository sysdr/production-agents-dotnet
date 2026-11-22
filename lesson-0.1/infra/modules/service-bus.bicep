// infra/modules/service-bus.bicep — POST 29
// Deploys: Azure Service Bus Namespace + Queue
// Local equivalent: servicebus-emulator (docker-compose.yml, port 5672)

param namespaceName    string
param location         string
param sku              string = 'Standard'
param queueName        string = 'orders'
param maxDeliveryCount int    = 3

resource sbNamespace 'Microsoft.ServiceBus/namespaces@2022-10-01-preview' = {
  name    : namespaceName
  location: location
  sku     : { name: sku }
  properties: { minimumTlsVersion: '1.2' }
}

resource sbQueue 'Microsoft.ServiceBus/namespaces/queues@2022-10-01-preview' = {
  parent: sbNamespace
  name  : queueName
  properties: {
    // POST 29: MaxDeliveryCount=3 matches servicebus-config.json and Post 9 notes.
    // Consistent across emulator and production — ensures DLQ behaviour is identical.
    maxDeliveryCount          : maxDeliveryCount
    requiresDuplicateDetection: true
    duplicateDetectionHistoryTimeWindow: 'PT10M'
    lockDuration              : 'PT1M'
    maxSizeInMegabytes        : 1024
  }
}

// POST 29: AuthorizationRule — Manage rights needed for Service Bus Emulator parity.
// In production, use Managed Identity + RBAC instead of connection strings.
// The connection string output is consumed by container-app.bicep secrets.
resource sbAuthRule 'Microsoft.ServiceBus/namespaces/authorizationRules@2022-10-01-preview' = {
  parent: sbNamespace
  name  : 'orders-app'
  properties: { rights: ['Listen', 'Send'] }
}

output namespaceName   string = sbNamespace.name
output connectionString string = sbAuthRule.listKeys().primaryConnectionString
