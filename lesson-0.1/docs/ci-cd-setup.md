# CI/CD Setup Guide — Post 30
# Distributed Systems with .NET — hyperscale-log-monitoring
#
# ONE-TIME SETUP: OIDC Federated Identity for GitHub Actions → Azure
#
# WHY OIDC (not a client secret):
#   A client secret stored in GitHub Secrets expires and must be rotated.
#   If it leaks, it grants Azure access until rotated.
#   OIDC issues a short-lived token (valid for the job duration only).
#   No long-lived credential ever exists in GitHub.
#
# STEP 1: Create an App Registration
#   az ad app create --display-name "hyperscale-log-monitoring-cicd"
#   # Note the appId (= AZURE_CLIENT_ID) and objectId
#
# STEP 2: Create a Service Principal
#   az ad sp create --id <appId>
#   # Note the tenantId (= AZURE_TENANT_ID)
#
# STEP 3: Assign Contributor role on the resource group
#   az role assignment create \
#     --assignee <appId> \
#     --role Contributor \
#     --scope /subscriptions/<subId>/resourceGroups/rg-orders-prod
#
# STEP 4: Create federated credential (trusts GitHub tokens for main branch)
#   az ad app federated-credential create \
#     --id <objectId> \
#     --parameters '{
#       "name": "github-main",
#       "issuer": "https://token.actions.githubusercontent.com",
#       "subject": "repo:YOUR_ORG/hyperscale-log-monitoring:ref:refs/heads/main",
#       "audiences": ["api://AzureADTokenExchange"]
#     }'
#
# STEP 5: Add GitHub Secrets (Settings → Secrets → Actions)
#   AZURE_CLIENT_ID       = <appId from step 1>
#   AZURE_TENANT_ID       = <tenantId from step 2>
#   AZURE_SUBSCRIPTION_ID = <your subscription ID>
#   AZURE_RESOURCE_GROUP  = rg-orders-prod
#   ACR_SERVER            = myregistry.azurecr.io
#   ACR_NAME              = myregistry
#   JWT_SIGNING_KEY       = <your signing key — use `openssl rand -base64 32`>
#
# ROLLBACK PROCEDURE:
#   # Redeploy a specific Git SHA:
#   az deployment group create \
#     --resource-group rg-orders-prod \
#     --template-file infra/main.bicep \
#     --parameters infra/parameters.bicepparam \
#     --parameters imageTag=<PREVIOUS_SHA> \
#     --parameters containerRegistryServer=myregistry.azurecr.io \
#     --parameters jwtSigningKey=$env:JWT_SIGNING_KEY
#
#   # The SHA tags in ACR make this possible — 'latest' would not.
#   # Find prior SHAs: git log --oneline -10
#
# ENVIRONMENT GATES (Settings → Environments → production):
#   Add required reviewers to the 'production' environment.
#   The deploy job in ci-cd.yml references 'environment: production' —
#   GitHub pauses the pipeline at that job and waits for approval.
#   Approval timeout: configure under environment settings (default: no timeout).
