// ─────────────────────────────────────────────────────────────
// Intune Wipe Monitor Report — Azure Function Infrastructure
// Deploys: Function App (Consumption), Storage Account,
//          Application Insights
// ─────────────────────────────────────────────────────────────

@description('Azure region for all resources')
param location string

@description('Prefix for resource names (3-12 chars, e.g. wipereport)')
@minLength(3)
@maxLength(12)
param prefix string

@description('Graph API Tenant ID')
param graphTenantId string

@description('Graph API Client ID')
param graphClientId string

@description('Graph API Client Secret')
@secure()
param graphClientSecret string

@description('SharePoint Site ID (graph.microsoft.com/v1.0/sites/{host}:/{path})')
param sharePointSiteId string = ''

@description('SharePoint Drive ID for the document library')
param sharePointDriveId string = ''

@description('SharePoint folder path for reports')
param sharePointFolderPath string = 'WipeMonitor Reports'

@description('Teams webhook URL for Adaptive Card notifications')
param teamsWebhookUrl string = ''

@description('Number of days to include in the report')
param reportDays int = 30

// ── Naming ──────────────────────────────────────────────────
var suffix = uniqueString(resourceGroup().id)
var names = {
  funcApp: '${prefix}-func'
  funcPlan: '${prefix}-func-plan'
  storage: replace('${prefix}st${take(suffix, 6)}', '-', '')
  appInsights: '${prefix}-report-insights'
  logAnalytics: '${prefix}-report-logs'
}

// ── Log Analytics (required by App Insights) ────────────────
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logAnalytics
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

// ── Application Insights ────────────────────────────────────
resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.appInsights
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ── Storage Account (required by Azure Functions) ───────────
resource storage 'Microsoft.Storage/storageAccounts@2023-05-01' = {
  name: names.storage
  location: location
  sku: { name: 'Standard_LRS' }
  kind: 'StorageV2'
  properties: {
    supportsHttpsTrafficOnly: true
    minimumTlsVersion: 'TLS1_2'
    allowSharedKeyAccess: true
  }
}

// ── Function App (Consumption plan) ─────────────────────────
resource funcPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: names.funcPlan
  location: location
  kind: 'linux'
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {
    reserved: true
  }
}

resource funcApp 'Microsoft.Web/sites@2023-12-01' = {
  name: names.funcApp
  location: location
  kind: 'functionapp,linux'
  identity: {
    type: 'SystemAssigned'
  }
  properties: {
    serverFarmId: funcPlan.id
    httpsOnly: true
    siteConfig: {
      minTlsVersion: '1.2'
      netFrameworkVersion: 'v10.0'
      appSettings: [
        { name: 'AzureWebJobsStorage__accountName', value: storage.name }
        { name: 'FUNCTIONS_EXTENSION_VERSION', value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME', value: 'dotnet-isolated' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'Graph__TenantId', value: graphTenantId }
        { name: 'Graph__ClientId', value: graphClientId }
        { name: 'Graph__ClientSecret', value: graphClientSecret }
        { name: 'Report__SharePointSiteId', value: sharePointSiteId }
        { name: 'Report__SharePointDriveId', value: sharePointDriveId }
        { name: 'Report__SharePointFolderPath', value: sharePointFolderPath }
        { name: 'Report__TeamsWebhookUrl', value: teamsWebhookUrl }
        { name: 'Report__ReportDays', value: string(reportDays) }
      ]
    }
  }
}

// Storage Blob Data Owner role for Function App Managed Identity
resource storageBlobRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, funcApp.id, 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  scope: storage
  properties: {
    principalId: funcApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', 'b7e6dc6d-f1e8-4753-8033-0f276bb0955b')
  }
}

// Storage Account Contributor role for Function App (file shares for consumption plan)
resource storageContribRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, funcApp.id, '17d1049b-9a84-46fb-8f53-869881c3d3ab')
  scope: storage
  properties: {
    principalId: funcApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '17d1049b-9a84-46fb-8f53-869881c3d3ab')
  }
}

// Storage Queue Data Contributor for Function App (triggers)
resource storageQueueRole 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(storage.id, funcApp.id, '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  scope: storage
  properties: {
    principalId: funcApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '974c5e8b-45b9-4653-ba55-5f855dd0fb88')
  }
}

// ── Outputs ─────────────────────────────────────────────────
output functionAppName string = funcApp.name
output functionAppHostname string = funcApp.properties.defaultHostName
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output storageAccountName string = storage.name
