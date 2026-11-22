// infra/modules/container-app.bicep — POST 29
// Deploys: one Azure Container App (used for order-service, order-processor, user-service)
// This single reusable module replaces a separate ARM resource block per service.

param appName      string
param location     string
param environmentId string
param image        string
param minReplicas  int    = 1
param maxReplicas  int    = 10
param secrets      array  = []
param env          array  = []
param targetPort   int    = 8080
param externalIngress bool = false

resource containerApp 'Microsoft.App/containerApps@2023-05-01' = {
  name    : appName
  location: location
  properties: {
    environmentId: environmentId
    configuration: {
      // POST 29: secrets block holds values; env vars reference secrets by name.
      // The value is encrypted at rest in Container Apps — never in the ARM template JSON.
      secrets: secrets
      ingress: {
        external  : externalIngress   // only order-service and user-service expose external ingress
        targetPort: targetPort
        transport : 'http'
        traffic   : [{ weight: 100, latestRevision: true }]
      }
    }
    template: {
      containers: [
        {
          name   : appName
          image  : image
          // POST 29: env array — mixes plain values and secretRef patterns.
          // secretRef: 'jwt-signing-key' pulls from the secrets block above.
          // value: 'dev' is a plain string — safe because it is not sensitive.
          env    : env
          resources: {
            cpu   : json('0.5')
            memory: '1Gi'
          }
        }
      ]
      scale: {
        // POST 29: minReplicas=1 prevents cold start (same as KEDA in Post 31).
        // KEDA ScaledObject (Post 31) overrides maxReplicas at runtime.
        minReplicas: minReplicas
        maxReplicas: maxReplicas
      }
    }
  }
}

output fqdn     string = containerApp.properties.configuration.ingress.fqdn
output appName  string = containerApp.name
