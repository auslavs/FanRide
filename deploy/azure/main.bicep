param location string = resourceGroup().location
param environment string = 'dev'
param tags object = {
  application: 'FanRide'
  environment: environment
}

@description('Name of the Azure Cosmos DB account to provision.')
param cosmosAccountName string = 'fanride${environment}'

@description('Whether to enable the Cosmos DB free tier (only one account per subscription can use it).')
param cosmosEnableFreeTier bool = true

@description('Name of the Azure App Service plan (Linux).')
param appServicePlanName string = 'fanride-asp-${environment}'

@description('Name of the backend App Service (API).')
param webAppName string = 'fanride-api-${environment}'

@description('Name of the Azure SignalR Service instance.')
param signalRName string = 'fanride-signalr-${environment}'

@description('Name of the Azure Static Web App for the frontend.')
param staticWebAppName string = 'fanride-frontend-${environment}'

@description('Name of the Application Insights component.')
param appInsightsName string = 'fanride-ai-${environment}'

@description('Azure region for the Static Web App (must be one of the supported locations).')
param staticWebAppLocation string = 'CentralUS'

var cosmosDatabaseName = 'fanride'
var readModelContainers = [
  {
    name: 'rm_match_state'
    partitionKey: '/matchId'
  }
  {
    name: 'rm_tes_history'
    partitionKey: '/matchId'
  }
  {
    name: 'rm_leaderboard'
    partitionKey: '/matchId'
  }
]

resource cosmosAccount 'Microsoft.DocumentDB/databaseAccounts@2023-04-15' = {
  name: cosmosAccountName
  location: location
  tags: tags
  kind: 'GlobalDocumentDB'
  properties: {
    consistencyPolicy: {
      defaultConsistencyLevel: 'Strong'
    }
    databaseAccountOfferType: 'Standard'
    enableFreeTier: cosmosEnableFreeTier
    locations: [
      {
        locationName: location
        failoverPriority: 0
        isZoneRedundant: false
      }
    ]
    publicNetworkAccess: 'Enabled'
    capabilities: [
      {
        name: 'EnableServerless'
      }
    ]
  }
}

resource cosmosSqlDatabase 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases@2023-04-15' = {
  name: '${cosmosAccount.name}/${cosmosDatabaseName}'
  tags: tags
  properties: {
    resource: {
      id: cosmosDatabaseName
    }
    options: {}
  }
}

resource eventStoreContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  name: '${cosmosSqlDatabase.name}/es'
  tags: tags
  properties: {
    resource: {
      id: 'es'
      partitionKey: {
        paths: [
          '/streamId'
        ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
      }
    }
    options: {}
  }
}

resource leasesContainer 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = {
  name: '${cosmosSqlDatabase.name}/leases'
  tags: tags
  properties: {
    resource: {
      id: 'leases'
      partitionKey: {
        paths: [
          '/id'
        ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
      }
    }
    options: {}
  }
}

resource readModelContainersResources 'Microsoft.DocumentDB/databaseAccounts/sqlDatabases/containers@2023-04-15' = [for container in readModelContainers: {
  name: '${cosmosSqlDatabase.name}/${container.name}'
  tags: tags
  properties: {
    resource: {
      id: container.name
      partitionKey: {
        paths: [
          container.partitionKey
        ]
        kind: 'Hash'
      }
      indexingPolicy: {
        indexingMode: 'consistent'
      }
    }
    options: {}
  }
}]

resource signalR 'Microsoft.SignalRService/signalR@2023-02-01' = {
  name: signalRName
  location: location
  tags: tags
  sku: {
    name: 'Free_F1'
    tier: 'Free'
    capacity: 1
  }
  properties: {
    features: [
      {
        flag: 'ServiceMode'
        value: 'Serverless'
      }
    ]
    cors: {
      allowedOrigins: [
        '*'
      ]
    }
  }
}

resource appServicePlan 'Microsoft.Web/serverfarms@2023-01-01' = {
  name: appServicePlanName
  location: location
  tags: tags
  kind: 'linux'
  sku: {
    name: 'F1'
    tier: 'Free'
    size: 'F1'
    capacity: 1
  }
  properties: {
    reserved: true
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: appInsightsName
  location: location
  tags: tags
  kind: 'web'
  properties: {
    Application_Type: 'web'
    Flow_Type: 'Bluefield'
    IngestionMode: 'ApplicationInsights'
  }
}

var cosmosEndpoint = cosmosAccount.properties.documentEndpoint
var cosmosKeys = listKeys(cosmosAccount.id, '2023-04-15')
var signalRKeys = listKeys(signalR.id, '2023-02-01')

resource webApp 'Microsoft.Web/sites@2023-01-01' = {
  name: webAppName
  location: location
  tags: tags
  kind: 'app,linux'
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|8.0'
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: environment
        }
        {
          name: 'Cosmos__AccountEndpoint'
          value: cosmosEndpoint
        }
        {
          name: 'Cosmos__AccountKey'
          value: cosmosKeys.primaryMasterKey
        }
        {
          name: 'SignalR__ConnectionString'
          value: signalRKeys.primaryConnectionString
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
      ]
    }
  }
  dependsOn: [
    appServicePlan
    cosmosAccount
    signalR
    appInsights
  ]
}

resource staticWebApp 'Microsoft.Web/staticSites@2022-09-01' = {
  name: staticWebAppName
  location: staticWebAppLocation
  tags: tags
  sku: {
    name: 'Free'
    tier: 'Free'
  }
  properties: {
    stagingEnvironmentPolicy: 'Enabled'
  }
}

var cosmosConnectionString = 'AccountEndpoint=${cosmosEndpoint};AccountKey=${cosmosKeys.primaryMasterKey};'

output cosmosAccountEndpoint string = cosmosEndpoint
output cosmosPrimaryKey string = cosmosKeys.primaryMasterKey
output cosmosConnectionString string = cosmosConnectionString
output signalRConnectionString string = signalRKeys.primaryConnectionString
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output staticWebAppDefaultHostname string = staticWebApp.properties.defaultHostname
output webAppHostname string = webApp.properties.defaultHostName
