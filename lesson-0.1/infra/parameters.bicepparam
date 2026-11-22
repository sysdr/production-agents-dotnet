// infra/parameters.bicepparam — POST 29
// Developer-local parameters for az deployment group create
//
// NEVER commit real values for @secure() params — use environment variables or
// Azure Key Vault parameter references in CI/CD (Post 30).
//
// Usage:
//   az deployment group create \
//     --resource-group rg-orders-dev \
//     --template-file infra/main.bicep \
//     --parameters infra/parameters.bicepparam \
//     --parameters jwtSigningKey=$env:JWT_SIGNING_KEY

using 'main.bicep'

param environmentName        = 'dev'
param location               = 'eastus'
param imageTag               = 'latest'
param containerRegistryServer = 'YOUR_REGISTRY.azurecr.io'

// POST 29: jwtSigningKey is @secure() — do NOT hardcode here.
// Override at deploy time: --parameters jwtSigningKey=$env:JWT_SIGNING_KEY
// In CI/CD (Post 30) this comes from GitHub Secrets via --parameters jwtSigningKey=${{ secrets.JWT_SIGNING_KEY }}
param jwtSigningKey = ''   // placeholder — always override at deploy time
