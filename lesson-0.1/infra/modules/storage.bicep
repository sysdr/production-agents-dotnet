// infra/modules/storage.bicep — POST 29
// Deploys: Azure Storage Account + Blob Containers + Tables
// Local equivalent: Azurite (docker-compose.yml, ports 10000/10001/10002)

param accountName    string
param location       string
param replication    string = 'LRS'
param blobContainers array  = []
param tables         array  = []

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name    : accountName
  location: location
  kind    : 'StorageV2'
  sku     : { name: '${replication == "GRS" ? "Standard_GRS" : "Standard_LRS"}' }
  properties: {
    minimumTlsVersion          : 'TLS1_2'
    allowBlobPublicAccess      : false
    supportsHttpsTrafficOnly   : true
  }
}

resource blobService 'Microsoft.Storage/storageAccounts/blobServices@2023-01-01' = {
  parent: storageAccount
  name  : 'default'
}

// POST 29: Create each blob container — matches OrderBlobStore processed-orders path.
resource containers 'Microsoft.Storage/storageAccounts/blobServices/containers@2023-01-01' = [
  for c in blobContainers: {
    parent: blobService
    name  : c
    properties: { publicAccess: 'None' }
  }
]

resource tableService 'Microsoft.Storage/storageAccounts/tableServices@2023-01-01' = {
  parent: storageAccount
  name  : 'default'
}

// POST 29: Create each table — matches TenantConfigStore tenantconfig table.
resource storageTables 'Microsoft.Storage/storageAccounts/tableServices/tables@2023-01-01' = [
  for t in tables: {
    parent: tableService
    name  : t
  }
]

output accountName     string = storageAccount.name
output connectionString string = 'DefaultEndpointsProtocol=https;AccountName=${storageAccount.name};AccountKey=${storageAccount.listKeys().keys[0].value};EndpointSuffix=core.windows.net'
