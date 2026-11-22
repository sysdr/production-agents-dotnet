// infra/modules/event-hubs.bicep — POST 29
// Deploys: Azure Event Hubs Namespace + Event Hub + Consumer Groups
// Local equivalent: eventhubs-emulator (docker-compose.yml, port 5673)

param namespaceName  string
param location       string
param sku            string   = 'Standard'
param eventHubName   string   = 'order-events'
param partitionCount int      = 4
param consumerGroups array    = []

resource ehNamespace 'Microsoft.EventHub/namespaces@2022-10-01-preview' = {
  name    : namespaceName
  location: location
  sku     : { name: sku, tier: sku, capacity: 1 }
  properties: { minimumTlsVersion: '1.2', isAutoInflateEnabled: false }
}

resource eventHub 'Microsoft.EventHub/namespaces/eventhubs@2022-10-01-preview' = {
  parent    : ehNamespace
  name      : eventHubName
  properties: {
    // POST 29: partitionCount=4 matches eventhubs-config.json.
    // Partition count cannot be changed after creation — set thoughtfully.
    // Rule of thumb: partitions >= expected max consumer instances.
    partitionCount          : partitionCount
    messageRetentionInDays  : 1   // dev default; increase to 7 for prod
  }
}

// POST 29: Consumer groups — one per downstream processor.
// Matches Post 13: order-processor group + analytics group.
resource groups 'Microsoft.EventHub/namespaces/eventhubs/consumergroups@2022-10-01-preview' = [
  for g in consumerGroups: {
    parent: eventHub
    name  : g
  }
]

resource ehAuthRule 'Microsoft.EventHub/namespaces/authorizationRules@2022-10-01-preview' = {
  parent    : ehNamespace
  name      : 'orders-app'
  properties: { rights: ['Listen', 'Send', 'Manage'] }
}

output namespaceName   string = ehNamespace.name
output connectionString string = ehAuthRule.listKeys().primaryConnectionString
