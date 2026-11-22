// infra/modules/container-apps-env.bicep — POST 29
// Deploys: Azure Container Apps Environment (shared runtime for all apps)
// Equivalent to: the Aspire local orchestrator (AppHost) in production

param environmentName string
param location        string

// POST 29: Log Analytics workspace — Container Apps uses it for structured logs.
// This is the production equivalent of the Aspire Dashboard (Post 5).
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2022-10-01' = {
  name    : '${environmentName}-logs'
  location: location
  properties: {
    sku            : { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource containerAppsEnv 'Microsoft.App/managedEnvironments@2023-05-01' = {
  name    : environmentName
  location: location
  properties: {
    appLogsConfiguration: {
      destination             : 'log-analytics'
      logAnalyticsConfiguration: {
        customerId: logAnalytics.properties.customerId
        sharedKey : logAnalytics.listKeys().primarySharedKey
      }
    }
  }
}

output environmentId string = containerAppsEnv.id
output environmentName string = containerAppsEnv.name
