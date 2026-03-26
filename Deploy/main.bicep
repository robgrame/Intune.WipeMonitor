// ─────────────────────────────────────────────────────────────
// Intune Wipe Monitor — Bicep Infrastructure
// Deploys: App Service, Key Vault, App Configuration,
//          Application Insights, VNet, Private Endpoints
// ─────────────────────────────────────────────────────────────

@description('Azure region for all resources')
param location string

@description('Prefix for resource names (e.g. wipemon)')
@minLength(3)
@maxLength(12)
param prefix string

@description('App Service SKU')
param appServiceSku string = 'B1'

@description('Application Insights connection string (output only)')
output appInsightsConnectionString string = appInsights.properties.ConnectionString

@description('Web App default hostname')
output webAppHostname string = webApp.properties.defaultHostName

@description('Web App Managed Identity principal ID')
output webAppPrincipalId string = webApp.identity.principalId

@description('Key Vault URI')
output keyVaultUri string = keyVault.properties.vaultUri

@description('App Configuration endpoint')
output appConfigEndpoint string = appConfig.properties.endpoint

// ── Naming ──────────────────────────────────────────────────
var suffix = uniqueString(resourceGroup().id)
var names = {
  appPlan: '${prefix}-plan'
  webApp: '${prefix}-app'
  keyVault: '${prefix}-kv-${take(suffix, 4)}'
  appConfig: '${prefix}-appconfig'
  appInsights: '${prefix}-insights'
  logAnalytics: '${prefix}-logs'
  vnet: '${prefix}-vnet'
  snetWebApp: 'snet-webapp'
  snetPE: 'snet-privateendpoints'
  sqlServer: '${prefix}-sql'
  sqlDb: '${prefix}-db'
}

// ── Log Analytics + Application Insights ────────────────────
resource logAnalytics 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: names.logAnalytics
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: 30
  }
}

resource appInsights 'Microsoft.Insights/components@2020-02-02' = {
  name: names.appInsights
  location: location
  kind: 'web'
  properties: {
    Application_Type: 'web'
    WorkspaceResourceId: logAnalytics.id
  }
}

// ── VNet + Subnets ──────────────────────────────────────────
resource vnet 'Microsoft.Network/virtualNetworks@2024-01-01' = {
  name: names.vnet
  location: location
  properties: {
    addressSpace: { addressPrefixes: ['10.0.0.0/16'] }
    subnets: [
      {
        name: names.snetWebApp
        properties: {
          addressPrefix: '10.0.1.0/24'
          delegations: [
            {
              name: 'webapp'
              properties: { serviceName: 'Microsoft.Web/serverFarms' }
            }
          ]
        }
      }
      {
        name: names.snetPE
        properties: {
          addressPrefix: '10.0.2.0/24'
        }
      }
    ]
  }
}

// ── App Service Plan + Web App ──────────────────────────────
resource appPlan 'Microsoft.Web/serverfarms@2024-04-01' = {
  name: names.appPlan
  location: location
  kind: 'linux'
  sku: { name: appServiceSku }
  properties: { reserved: true }
}

resource webApp 'Microsoft.Web/sites@2024-04-01' = {
  name: names.webApp
  location: location
  identity: { type: 'SystemAssigned' }
  properties: {
    serverFarmId: appPlan.id
    httpsOnly: true
    virtualNetworkSubnetId: vnet.properties.subnets[0].id
    siteConfig: {
      linuxFxVersion: 'DOTNETCORE|10.0'
      alwaysOn: true
      minTlsVersion: '1.2'
      appSettings: [
        { name: 'APPLICATIONINSIGHTS_CONNECTION_STRING', value: appInsights.properties.ConnectionString }
        { name: 'AppConfig__Endpoint', value: appConfig.properties.endpoint }
        { name: 'WEBSITE_DNS_SERVER', value: '168.63.129.16' }
        { name: 'WEBSITE_VNET_ROUTE_ALL', value: '1' }
        { name: 'WEBSITES_CONTAINER_START_TIME_LIMIT', value: '600' }
        { name: 'Database__Path', value: '/home/wipemonitor.db' }
      ]
    }
  }
}

// ── Key Vault ───────────────────────────────────────────────
resource keyVault 'Microsoft.KeyVault/vaults@2024-04-01-preview' = {
  name: names.keyVault
  location: location
  properties: {
    sku: { family: 'A', name: 'standard' }
    tenantId: subscription().tenantId
    enableRbacAuthorization: true
    publicNetworkAccess: 'Disabled'
    networkAcls: { defaultAction: 'Deny' }
  }
}

// ── App Configuration ───────────────────────────────────────
resource appConfig 'Microsoft.AppConfiguration/configurationStores@2024-05-01' = {
  name: names.appConfig
  location: location
  sku: { name: 'Standard' }
  properties: {
    publicNetworkAccess: 'Disabled'
  }
}

// ── Private DNS Zones ───────────────────────────────────────
resource dnsKv 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.vaultcore.azure.net'
  location: 'global'
}

resource dnsAppConfig 'Microsoft.Network/privateDnsZones@2024-06-01' = {
  name: 'privatelink.azconfig.io'
  location: 'global'
}

// DNS Zone → VNet Links
resource dnsLinkKv 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: dnsKv
  name: 'kv-link'
  location: 'global'
  properties: {
    virtualNetwork: { id: vnet.id }
    registrationEnabled: false
  }
}

resource dnsLinkAppConfig 'Microsoft.Network/privateDnsZones/virtualNetworkLinks@2024-06-01' = {
  parent: dnsAppConfig
  name: 'appconfig-link'
  location: 'global'
  properties: {
    virtualNetwork: { id: vnet.id }
    registrationEnabled: false
  }
}

// ── Private Endpoints ───────────────────────────────────────
resource peKv 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: 'pe-${names.keyVault}'
  location: location
  properties: {
    subnet: { id: vnet.properties.subnets[1].id }
    privateLinkServiceConnections: [
      {
        name: 'kv-connection'
        properties: {
          privateLinkServiceId: keyVault.id
          groupIds: ['vault']
        }
      }
    ]
  }
}

resource peKvDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: peKv
  name: 'kv-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'keyvault'
        properties: { privateDnsZoneId: dnsKv.id }
      }
    ]
  }
}

resource peAppConfig 'Microsoft.Network/privateEndpoints@2024-01-01' = {
  name: 'pe-${names.appConfig}'
  location: location
  properties: {
    subnet: { id: vnet.properties.subnets[1].id }
    privateLinkServiceConnections: [
      {
        name: 'appconfig-connection'
        properties: {
          privateLinkServiceId: appConfig.id
          groupIds: ['configurationStores']
        }
      }
    ]
  }
}

resource peAppConfigDns 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups@2024-01-01' = {
  parent: peAppConfig
  name: 'appconfig-dns-group'
  properties: {
    privateDnsZoneConfigs: [
      {
        name: 'appconfig'
        properties: { privateDnsZoneId: dnsAppConfig.id }
      }
    ]
  }
}

// ── RBAC Role Assignments ───────────────────────────────────
// Key Vault Secrets User → Web App MI
resource kvSecretsUser 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(keyVault.id, webApp.id, '4633458b-17de-408a-b874-0445c86b69e6')
  scope: keyVault
  properties: {
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '4633458b-17de-408a-b874-0445c86b69e6') // Key Vault Secrets User
  }
}

// App Configuration Data Reader → Web App MI
resource appConfigReader 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(appConfig.id, webApp.id, '516239f1-63e1-4d78-a4de-a74fb236a071')
  scope: appConfig
  properties: {
    principalId: webApp.identity.principalId
    principalType: 'ServicePrincipal'
    roleDefinitionId: subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '516239f1-63e1-4d78-a4de-a74fb236a071') // App Configuration Data Reader
  }
}
