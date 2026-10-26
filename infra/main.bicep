targetScope = 'subscription'

@description('Location for the resource group and the storage account')
param location string = 'eastus2'

@description('App Service Plan SKU. Left at F1 (Free) unless somebody deliberately opts into a billed tier; see resources.bicep for what each one buys.')
param appServicePlanSku string = 'F1'

@description('Location for the App Service Plan and Web App (defaults to westus2 due to F1 quota)')
param webAppLocation string = 'westus2'

// Naming: Po{Solution}. Everything app-specific lives in the PoFightJudge resource group; Key Vault,
// App Insights and Log Analytics stay in PoShared and are referenced as existing, the same layout every
// other Po* app uses.
var resourceGroupName = 'PoFightJudge'
var sharedResourceGroupName = 'PoShared'
var storageAccountName = 'stpofightjudge2'
var webAppName = 'app-pofightjudge2'
var appServicePlanName = 'asp-pofightjudge-f1'
var keyVaultName = 'kv-poshared2'
var appInsightsName = 'poappideinsights8f9c9a4e'

resource rg 'Microsoft.Resources/resourceGroups@2021-04-01' = {
  name: resourceGroupName
  location: location
}

module resources 'resources.bicep' = {
  name: 'resources'
  scope: rg
  params: {
    webAppLocation: webAppLocation
    storageLocation: location
    storageAccountName: storageAccountName
    webAppName: webAppName
    appServicePlanName: appServicePlanName
    appServicePlanSku: appServicePlanSku
    keyVaultName: keyVaultName
    appInsightsName: appInsightsName
    sharedResourceGroupName: sharedResourceGroupName
  }
}

output AZURE_RESOURCE_GROUP string = rg.name
output AZURE_STORAGE_ACCOUNT_NAME string = resources.outputs.storageAccountName
output SERVICE_API_URL string = resources.outputs.webAppUrl
output WEB_APP_PRINCIPAL_ID string = resources.outputs.webAppPrincipalId
