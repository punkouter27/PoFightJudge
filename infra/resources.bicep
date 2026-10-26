@description('Location for the App Service Plan and Web App (must match each other)')
param webAppLocation string

@description('Location for the storage account')
param storageLocation string

@description('Storage account for Table (Matches/Turns/Analyses/Results/Profiles/Fighters) and Blob (audio, faces, TTS cache) — created here')
param storageAccountName string

@description('App Service (Web App) name')
param webAppName string

@description('App Service Plan name, Linux, created here')
param appServicePlanName string

@description('''
App Service Plan SKU. F1 (Free) is one shared core with a 60 CPU-minute daily quota, no Always On and no
scale-out — and a live fight lives in the API process with an in-process SignalR hub, so a second instance
would not see it anyway. That caps the app at a couple of concurrent fights. B1 lifts the CPU quota and
allows Always On (cold starts disappear); anything above one instance additionally needs the session registry
moved out of memory and a SignalR backplane. Changing this off F1 starts billing, so it is a deliberate switch.
''')
@allowed([
  'F1'
  'B1'
  'B2'
  'S1'
  'P0v3'
])
param appServicePlanSku string = 'F1'

@description('Key Vault in PoShared holding the PoFightJudge--* secrets')
param keyVaultName string

@description('Shared Application Insights component in PoShared')
param appInsightsName string

@description('The PoShared resource group')
param sharedResourceGroupName string

// ─────────────────────────────────────────────
// Shared resources (existing, PoShared)
// ─────────────────────────────────────────────

resource sharedKeyVault 'Microsoft.KeyVault/vaults@2023-07-01' existing = {
  name: keyVaultName
  scope: resourceGroup(sharedResourceGroupName)
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' existing = {
  name: appInsightsName
  scope: resourceGroup(sharedResourceGroupName)
}

// ─────────────────────────────────────────────
// Storage: Table + Blob, reached by the web app's managed identity. Shared-key access is off, which is not
// belt and braces — the app has no connection-string path at all, and BannedSymbols.txt makes one a build
// error. Containers and tables are created by the app on first use.
// ─────────────────────────────────────────────

resource storageAccount 'Microsoft.Storage/storageAccounts@2023-01-01' = {
  name: storageAccountName
  location: storageLocation
  kind: 'StorageV2'
  sku: {
    name: 'Standard_LRS'
  }
  properties: {
    minimumTlsVersion: 'TLS1_2'
    supportsHttpsTrafficOnly: true
    allowBlobPublicAccess: false
    allowSharedKeyAccess: false
    accessTier: 'Hot'
  }
}

// ─────────────────────────────────────────────
// App Service Plan — Linux (`reserved: true` is what makes it Linux). Free by default; see appServicePlanSku.
// ─────────────────────────────────────────────

var planTiers = {
  F1: 'Free'
  B1: 'Basic'
  B2: 'Basic'
  S1: 'Standard'
  P0v3: 'PremiumV3'
}

var isFreePlan = appServicePlanSku == 'F1'

resource appServicePlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: appServicePlanName
  location: webAppLocation
  sku: {
    name: appServicePlanSku
    tier: planTiers[appServicePlanSku]
  }
  properties: {
    reserved: true
  }
}

// ─────────────────────────────────────────────
// Web App — system-assigned identity; secrets from kv-poshared2 via DefaultAzureCredential.
// WebSockets must be on for the SignalR audio hub; the Gemini Live socket is opened server-side and does not
// care. Always On is not available on F1 (ARM rejects the write if set), so it follows the SKU.
// ─────────────────────────────────────────────

resource webApp 'Microsoft.Web/sites@2023-12-01' = {
  name: webAppName
  location: webAppLocation
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: appServicePlan.id
    httpsOnly: true
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      // The publish folder carries two runtimeconfig.json files (API + WASM client), so Oryx cannot infer the
      // startup DLL and falls back to its placeholder site (every route 404). Name it explicitly.
      appCommandLine: 'dotnet PoFightJudge.Api.dll'
      alwaysOn: !isFreePlan
      webSocketsEnabled: true
      ftpsState: 'Disabled'
      minTlsVersion: '1.2'
      http20Enabled: true
      appSettings: [
        {
          name: 'ASPNETCORE_ENVIRONMENT'
          value: 'Production'
        }
        {
          name: 'ASPNETCORE_URLS'
          value: 'http://+:8080'
        }
        {
          name: 'KeyVault__Uri'
          value: sharedKeyVault.properties.vaultUri
        }
        // Endpoints, not connection strings: DefaultAzureCredential is the only storage credential there is.
        {
          name: 'PoFightJudge__TableStorageEndpoint'
          value: storageAccount.properties.primaryEndpoints.table
        }
        {
          name: 'PoFightJudge__BlobStorageEndpoint'
          value: storageAccount.properties.primaryEndpoints.blob
        }
        {
          name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'
          value: appInsights.properties.ConnectionString
        }
        {
          name: 'AllowedHosts'
          value: '${webAppName}.azurewebsites.net'
        }
      ]
    }
  }
}

// Data-plane roles for the identity (Table + Blob). Module because the assignment name is seeded with
// principalId, which only exists once the site is created — see storage-role.bicep.
module storageRoles 'storage-role.bicep' = {
  name: 'storage-roles'
  params: {
    storageAccountName: storageAccount.name
    principalId: webApp.identity.principalId
  }
}

// Secret get/list on kv-poshared2; without it the host cannot load PoFightJudge--* and comes up degraded.
module keyVaultAccess 'keyvault-access.bicep' = {
  name: 'keyvault-access'
  scope: resourceGroup(sharedResourceGroupName)
  params: {
    keyVaultName: keyVaultName
    principalId: webApp.identity.principalId
    tenantId: subscription().tenantId
  }
}

output storageAccountName string = storageAccount.name
output webAppUrl string = 'https://${webApp.properties.defaultHostName}'
output webAppPrincipalId string = webApp.identity.principalId
