# Azure deployment scaffolding

This folder provides Bicep infrastructure-as-code for provisioning the FanRide backend, real-time services, and frontend host on Azure using the lowest-cost (free where available) SKUs.

## Resources provisioned

The `main.bicep` template deploys:

- **Azure Cosmos DB (serverless + free tier)** – Strong consistency, single-region, and containers for the event store, read models, and leases.
- **Azure SignalR Service (Free_F1)** – Configured for serverless mode to back the SignalR hub used by the backend and clients.
- **App Service Plan (Linux, F1 Free)** – Hosts the backend ASP.NET Core application on the cheapest shared tier.
- **App Service (backend API)** – Linux .NET 8 web app with app settings populated for Cosmos DB, SignalR, and Application Insights.
- **Application Insights** – Collects telemetry emitted via OpenTelemetry exporters.
- **Azure Static Web App (Free)** – Serves the Fable frontend with built-in staging environments.

All resources inherit a consistent set of tags (`application`, `environment`) for cost tracking.

## Prerequisites

- Azure CLI `>= 2.50`
- Logged into the correct subscription: `az login`
- Resource group created in a supported region (example uses `eastus`):

  ```bash
  az group create --name fanride-dev-rg --location eastus
  ```

> **Note:** Azure Cosmos DB free tier can be enabled only once per subscription. If you have already consumed the free tier, pass `cosmosEnableFreeTier=false` during deployment.

## Deploying

Run a resource-group deployment with parameter overrides for your environment identifiers. The template defaults target development-friendly names; provide your own to ensure global uniqueness.

```bash
az deployment group create \
  --resource-group fanride-dev-rg \
  --template-file main.bicep \
  --parameters \
      environment=dev \
      cosmosAccountName=fanridedev01 \
      appServicePlanName=fanride-asp-dev01 \
      webAppName=fanride-api-dev01 \
      signalRName=fanride-signalr-dev01 \
      staticWebAppName=fanride-frontend-dev01 \
      appInsightsName=fanride-ai-dev01 \
      staticWebAppLocation=EastUS2 \
      cosmosEnableFreeTier=true
```

### Post-deployment configuration

- Deploy the backend via `az webapp deploy` or CI/CD, ensuring the managed identity or publish profile is stored securely.
- Configure the Azure Static Web App build workflow or upload the pre-built frontend assets with `az staticwebapp upload`.
- Rotate and store the Cosmos DB and SignalR connection strings in Azure Key Vault for production workloads; the outputs from the deployment surface them for initial setup.

## Tear down

To remove all provisioned Azure resources, delete the resource group:

```bash
az group delete --name fanride-dev-rg --yes --no-wait
```

This command prevents lingering charges by removing the free-tier resources when no longer required.
