<#
.SYNOPSIS
    Provisiona l'intera infrastruttura Intune Wipe Monitor:
    App Registrations, risorse Azure (Bicep), configurazione e RBAC.

.DESCRIPTION
    Questo script esegue in sequenza:
    1. Crea le App Registration (Graph API + Portal Auth) con ruoli e permessi
    2. Deploy Bicep (VNet, App Service, Key Vault, App Config, App Insights, Private Endpoints)
    3. Configura Key Vault secrets e App Configuration values
    4. Assegna l'utente corrente al ruolo WipeMonitor-Admin

    L'unico input richiesto è la region Azure e il prefisso per i nomi delle risorse.

.PARAMETER Location
    Azure region (es. westeurope, northeurope, westus2)

.PARAMETER Prefix
    Prefisso per i nomi delle risorse (3-12 caratteri, es. wipemon)

.PARAMETER ResourceGroup
    Nome del resource group. Default: {Prefix}-RG

.PARAMETER SkipBicep
    Se specificato, salta il deploy Bicep (utile per ri-eseguire solo la configurazione)

.EXAMPLE
    .\Deploy-Infrastructure.ps1 -Location westeurope -Prefix wipemon

.EXAMPLE
    .\Deploy-Infrastructure.ps1 -Location northeurope -Prefix mywipe -ResourceGroup MyCustomRG
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Location,

    [Parameter(Mandatory)]
    [ValidateLength(3, 12)]
    [string]$Prefix,

    [string]$ResourceGroup = "${Prefix}-RG",

    [switch]$SkipBicep
)

$ErrorActionPreference = "Stop"
$InformationPreference = "Continue"

# ── Banner ──────────────────────────────────────────────────
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Intune Wipe Monitor — Infrastructure Provisioning   ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""
Write-Host "  Region:         $Location"
Write-Host "  Prefix:         $Prefix"
Write-Host "  Resource Group: $ResourceGroup"
Write-Host ""

# ── Verifiche ───────────────────────────────────────────────
Write-Host "[CHECK] Verifica Azure CLI login..." -ForegroundColor Yellow
$account = az account show -o json 2>$null | ConvertFrom-Json
if (-not $account) { Write-Error "Non sei loggato in Azure CLI. Esegui 'az login' prima." }
Write-Host "  Subscription: $($account.name) ($($account.id))" -ForegroundColor Green
Write-Host "  Tenant:       $($account.tenantId)" -ForegroundColor Green

$tenantId = $account.tenantId
$subscriptionId = $account.id
$myObjectId = az ad signed-in-user show --query id -o tsv 2>$null
Write-Host "  User:         $myObjectId" -ForegroundColor Green

# ════════════════════════════════════════════════════════════
# STEP 1: App Registrations
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 1: App Registrations ═══" -ForegroundColor Cyan

# ── 1a. Graph API App Registration ──
Write-Host "[APP-REG] Creazione Intune.WipeMonitor (Graph API)..." -ForegroundColor Yellow
$graphApp = az ad app create --display-name "Intune.WipeMonitor" --sign-in-audience AzureADMyOrg -o json 2>$null | ConvertFrom-Json
$graphClientId = $graphApp.appId
$graphObjectId = $graphApp.id
Write-Host "  Client ID: $graphClientId" -ForegroundColor Green

# Permessi Graph
$graphResourceId = "00000003-0000-0000-c000-000000000000"
# DeviceManagementManagedDevices.ReadWrite.All
az ad app permission add --id $graphObjectId --api $graphResourceId --api-permissions "243333ab-4d21-40cb-a475-36241daa0842=Role" 2>$null
# DeviceManagementRBAC.Read.All
az ad app permission add --id $graphObjectId --api $graphResourceId --api-permissions "58ca0d9a-1575-47e1-a3cb-007ef2e4583b=Role" 2>$null
# DeviceManagementServiceConfig.ReadWrite.All (Autopilot cleanup)
az ad app permission add --id $graphObjectId --api $graphResourceId --api-permissions "5ac13192-7ace-4fcf-b828-1a26f28068ee=Role" 2>$null
Write-Host "  Permessi Graph aggiunti" -ForegroundColor Green

# Client secret
$graphSecret = az ad app credential reset --id $graphObjectId --display-name "Provisioned" --years 2 -o json 2>$null | ConvertFrom-Json
Write-Host "  Client Secret creato" -ForegroundColor Green

# Service Principal + Role Assignments
$graphSpRaw = az ad sp show --id $graphClientId -o json 2>$null
if (-not $graphSpRaw) {
    $graphSpRaw = az ad sp create --id $graphClientId -o json 2>$null
}
$graphSpId = ($graphSpRaw | ConvertFrom-Json).id

$msGraphSpId = (az ad sp show --id $graphResourceId -o json 2>$null | ConvertFrom-Json).id

# Grant app roles
foreach ($roleId in @("243333ab-4d21-40cb-a475-36241daa0842", "58ca0d9a-1575-47e1-a3cb-007ef2e4583b")) {
    $body = "{`"principalId`":`"$graphSpId`",`"resourceId`":`"$msGraphSpId`",`"appRoleId`":`"$roleId`"}"
    $body | Out-File -Encoding utf8NoBOM "$env:TEMP\role.json"
    az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$graphSpId/appRoleAssignments" --body "@$env:TEMP\role.json" -o none 2>$null
}
Write-Host "  App roles assegnati (admin consent)" -ForegroundColor Green

# ── 1b. Portal Auth App Registration ──
Write-Host ""
Write-Host "[APP-REG] Creazione Intune.WipeMonitor.Portal (Auth)..." -ForegroundColor Yellow
$portalApp = az ad app create `
    --display-name "Intune.WipeMonitor.Portal" `
    --sign-in-audience AzureADMyOrg `
    --web-redirect-uris "https://${Prefix}-app.azurewebsites.net/signin-oidc" "https://localhost:5001/signin-oidc" `
    --enable-id-token-issuance true `
    -o json 2>$null | ConvertFrom-Json
$portalClientId = $portalApp.appId
$portalObjectId = $portalApp.id
Write-Host "  Client ID: $portalClientId" -ForegroundColor Green

# App Role: WipeMonitor-Admin
$roleId = [guid]::NewGuid().ToString()
$appRoles = "[{`"id`":`"$roleId`",`"allowedMemberTypes`":[`"User`"],`"displayName`":`"WipeMonitor Admin`",`"description`":`"Full access to Wipe Monitor dashboard`",`"value`":`"WipeMonitor-Admin`",`"isEnabled`":true}]"
$appRoles | Out-File -Encoding utf8NoBOM "$env:TEMP\approles.json"
az ad app update --id $portalObjectId --app-roles "@$env:TEMP\approles.json" 2>$null
Write-Host "  App Role WipeMonitor-Admin creato" -ForegroundColor Green

# Portal secret
$portalSecret = az ad app credential reset --id $portalObjectId --display-name "Provisioned" --years 2 -o json 2>$null | ConvertFrom-Json
Write-Host "  Client Secret creato" -ForegroundColor Green

# Service Principal
$portalSpRaw = az ad sp show --id $portalClientId -o json 2>$null
if (-not $portalSpRaw) {
    $portalSpRaw = az ad sp create --id $portalClientId -o json 2>$null
}
$portalSpId = ($portalSpRaw | ConvertFrom-Json).id

# Assign current user to WipeMonitor-Admin role
$userRoleBody = "{`"principalId`":`"$myObjectId`",`"resourceId`":`"$portalSpId`",`"appRoleId`":`"$roleId`"}"
$userRoleBody | Out-File -Encoding utf8NoBOM "$env:TEMP\userrole.json"
az rest --method POST --url "https://graph.microsoft.com/v1.0/servicePrincipals/$portalSpId/appRoleAssignedTo" --body "@$env:TEMP\userrole.json" -o none 2>$null
Write-Host "  Utente corrente assegnato a WipeMonitor-Admin" -ForegroundColor Green

# ════════════════════════════════════════════════════════════
# STEP 2: Bicep Deployment
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 2: Deploy Infrastruttura (Bicep) ═══" -ForegroundColor Cyan

# Resource Group
az group create --name $ResourceGroup --location $Location -o none 2>$null
Write-Host "[BICEP] Resource Group: $ResourceGroup" -ForegroundColor Green

if (-not $SkipBicep) {
    $bicepPath = Join-Path $PSScriptRoot "main.bicep"

    Write-Host "[BICEP] Deploying $bicepPath ..." -ForegroundColor Yellow
    $deployment = az deployment group create `
        --resource-group $ResourceGroup `
        --template-file $bicepPath `
        --parameters location=$Location prefix=$Prefix `
        --query "properties.outputs" -o json 2>&1

    $outputs = $deployment | ConvertFrom-Json
    Write-Host "[BICEP] Deploy completato!" -ForegroundColor Green
    Write-Host "  Web App:     $($outputs.webAppHostname.value)" -ForegroundColor Green
    Write-Host "  Key Vault:   $($outputs.keyVaultUri.value)" -ForegroundColor Green
    Write-Host "  App Config:  $($outputs.appConfigEndpoint.value)" -ForegroundColor Green
    Write-Host "  App Insights configured" -ForegroundColor Green
} else {
    Write-Host "[BICEP] Skipped (flag -SkipBicep)" -ForegroundColor DarkGray
}

# ════════════════════════════════════════════════════════════
# STEP 3: Configurazione (Key Vault + App Config + Web App)
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 3: Configurazione ═══" -ForegroundColor Cyan

$kvName = az keyvault list -g $ResourceGroup --query "[0].name" -o tsv 2>$null
$appConfigName = az appconfig list -g $ResourceGroup --query "[0].name" -o tsv 2>$null
$webAppName = "${Prefix}-app"
$aiConnStr = (az monitor app-insights component show -g $ResourceGroup --query "[0].connectionString" -o tsv 2>$null)

# Temporarily enable public access for configuration
Write-Host "[CONFIG] Abilitazione temporanea accesso pubblico..." -ForegroundColor Yellow
az keyvault update --name $kvName -g $ResourceGroup --public-network-access Enabled -o none 2>$null
az appconfig update --name $appConfigName -g $ResourceGroup --enable-public-network true -o none 2>$null

# Assign myself as Key Vault admin to write secrets
$kvId = az keyvault show --name $kvName -g $ResourceGroup --query id -o tsv 2>$null
az role assignment create --assignee-object-id $myObjectId --assignee-principal-type User `
    --role "Key Vault Secrets Officer" --scope $kvId -o none 2>$null
$appConfigId = az appconfig show --name $appConfigName -g $ResourceGroup --query id -o tsv 2>$null
az role assignment create --assignee-object-id $myObjectId --assignee-principal-type User `
    --role "App Configuration Data Owner" --scope $appConfigId -o none 2>$null

Start-Sleep -Seconds 15

# Key Vault: store Graph client secret
Write-Host "[CONFIG] Storing secrets in Key Vault..." -ForegroundColor Yellow
az keyvault secret set --vault-name $kvName --name "Graph--ClientSecret" --value "$($graphSecret.password)" -o none 2>$null

# App Configuration: store all settings
Write-Host "[CONFIG] Storing config in App Configuration..." -ForegroundColor Yellow
$configKeys = @(
    @{ key="WipeMonitor:PollingIntervalMinutes"; value="60" }
    @{ key="WipeMonitor:RequireApproval"; value="true" }
    @{ key="WipeMonitor:CleanupIntune"; value="true" }
    @{ key="Graph:TenantId"; value=$tenantId }
    @{ key="Graph:ClientId"; value=$graphClientId }
    @{ key="Graph:Scope"; value="https://graph.microsoft.com/.default" }
    @{ key="ApplicationInsights:ConnectionString"; value=$aiConnStr }
    @{ key="WipeMonitor:Sentinel"; value="1" }
)
foreach ($kv in $configKeys) {
    az appconfig kv set --name $appConfigName --key $kv.key --value $kv.value --yes -o none 2>$null
}

# Key Vault reference for Graph secret
$secretUri = "https://$kvName.vault.azure.net/secrets/Graph--ClientSecret"
az appconfig kv set-keyvault --name $appConfigName --key "Graph:ClientSecret" --secret-identifier $secretUri --yes -o none 2>$null
Write-Host "[CONFIG] App Configuration populated" -ForegroundColor Green

# Web App: set auth settings
Write-Host "[CONFIG] Setting Web App auth settings..." -ForegroundColor Yellow
az webapp config appsettings set --name $webAppName -g $ResourceGroup `
    --settings `
        "AzureAd__Instance=https://login.microsoftonline.com/" `
        "AzureAd__TenantId=$tenantId" `
        "AzureAd__ClientId=$portalClientId" `
        "AzureAd__ClientSecret=$($portalSecret.password)" `
        "AzureAd__CallbackPath=/signin-oidc" `
        "Graph__TenantId=$tenantId" `
        "Graph__ClientId=$graphClientId" `
        "Graph__ClientSecret=$($graphSecret.password)" `
        "Graph__Scope=https://graph.microsoft.com/.default" `
    -o none 2>$null
Write-Host "[CONFIG] Web App configured" -ForegroundColor Green

# Disable public access
Write-Host "[CONFIG] Disabilitazione accesso pubblico..." -ForegroundColor Yellow
az keyvault update --name $kvName -g $ResourceGroup --public-network-access Disabled -o none 2>$null
az appconfig update --name $appConfigName -g $ResourceGroup --enable-public-network false -o none 2>$null

# ════════════════════════════════════════════════════════════
# DONE
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  ✅ Provisioning completato!                         ║" -ForegroundColor Green
Write-Host "╚═══════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  ── Risorse ──────────────────────────────────────────"
Write-Host "  Resource Group:  $ResourceGroup"
Write-Host "  Web App:         https://${webAppName}.azurewebsites.net"
Write-Host "  Key Vault:       $kvName"
Write-Host "  App Config:      $appConfigName"
Write-Host ""
Write-Host "  ── App Registrations ────────────────────────────────"
Write-Host "  Graph API:       $graphClientId (Intune.WipeMonitor)"
Write-Host "  Portal Auth:     $portalClientId (Intune.WipeMonitor.Portal)"
Write-Host "  Admin Role:      WipeMonitor-Admin (assegnato a te)"
Write-Host ""
Write-Host "  ── Prossimi passi ───────────────────────────────────"
Write-Host "  1. Pubblica la web app:"
Write-Host "     cd src/Intune.WipeMonitor && dotnet publish -c Release -o publish"
Write-Host "     az webapp deploy --name $webAppName -g $ResourceGroup --src-path publish.zip --type zip"
Write-Host ""
Write-Host "  2. Configura e installa l'agent on-prem:"
Write-Host "     Vedi SESSION-HANDOFF.md per i dettagli"
Write-Host ""
