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
  }
}

// ── Function App (Consumption plan) ─────────────────────────
resource funcPlan 'Microsoft.Web/serverfarms@2023-12-01' = {
  name: names.funcPlan
  location: location
  sku: {
    name: 'Y1'
    tier: 'Dynamic'
  }
  properties: {}
}

resource funcApp 'Microsoft.Web/sites@2023-12-01' = {
  name: names.funcApp
  location: location
  kind: 'functionapp'
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
        { name: 'AzureWebJobsStorage'; value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};EndpointSuffix=core.windows.net;AccountKey=${storage.listKeys().keys[0].value}' }
        { name: 'WEBSITE_CONTENTAZUREFILECONNECTIONSTRING'; value: 'DefaultEndpointsProtocol=https;AccountName=${storage.name};EndpointSuffix=core.windows.net;AccountKey=${storage.listKeys().keys[0].value}' }
        { name: 'WEBSITE_CONTENTSHARE'; value: toLower(names.funcApp) }
        { name: 'FUNCTIONS_EXTENSION_VERSION'; value: '~4' }
        { name: 'FUNCTIONS_WORKER_RUNTIME'; value: 'dotnet-isolated' }
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING'; value: appInsights.properties.ConnectionString }
        // Graph settings
        { name: 'Graph__TenantId'; value: graphTenantId }
        { name: 'Graph__ClientId'; value: graphClientId }
        { name: 'Graph__ClientSecret'; value: graphClientSecret }
        // Report settings
        { name: 'Report__SharePointSiteId'; value: sharePointSiteId }
        { name: 'Report__SharePointDriveId'; value: sharePointDriveId }
        { name: 'Report__SharePointFolderPath'; value: sharePointFolderPath }
        { name: 'Report__TeamsWebhookUrl'; value: teamsWebhookUrl }
        { name: 'Report__ReportDays'; value: string(reportDays) }
      ]
    }
  }
}

// ── Outputs ─────────────────────────────────────────────────
output functionAppName string = funcApp.name
output functionAppHostname string = funcApp.properties.defaultHostName
output appInsightsConnectionString string = appInsights.properties.ConnectionString
output storageAccountName string = storage.name
