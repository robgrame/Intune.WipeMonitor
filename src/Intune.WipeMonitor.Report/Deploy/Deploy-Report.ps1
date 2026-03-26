<#
.SYNOPSIS
    Deploy Intune Wipe Monitor Report — Azure Function + App Registration.

.DESCRIPTION
    1. Crea/aggiorna App Registration con i permessi Graph necessari
       (o riusa quella esistente di Intune.WipeMonitor)
    2. Deploy Bicep (Function App, Storage, App Insights)
    3. Pubblica la Function App
    4. Mostra i comandi per recuperare SharePoint Site/Drive ID

.PARAMETER Location
    Azure region (es. westeurope, westus2)

.PARAMETER Prefix
    Prefisso per i nomi delle risorse (3-12 caratteri)

.PARAMETER ResourceGroup
    Nome del resource group. Default: {Prefix}-Report-RG

.PARAMETER GraphAppId
    Client ID dell'app registration esistente (Intune.WipeMonitor).
    Se omesso, ne crea una nuova dedicata al report.

.PARAMETER TeamsWebhookUrl
    URL del webhook Power Automate per notifiche Teams.

.PARAMETER SharePointSiteUrl
    URL del sito SharePoint (es. https://contoso.sharepoint.com/sites/IT).
    Lo script recupera automaticamente SiteId e DriveId.

.EXAMPLE
    .\Deploy-Report.ps1 -Location westus2 -Prefix wipereport -SharePointSiteUrl "https://contoso.sharepoint.com/sites/IT"

.EXAMPLE
    .\Deploy-Report.ps1 -Location westeurope -Prefix wipereport -GraphAppId "f3baa699-2f8c-46e0-8e99-925fddb69030"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Location,

    [Parameter(Mandatory)]
    [ValidateLength(3, 12)]
    [string]$Prefix,

    [string]$ResourceGroup = "${Prefix}-Report-RG",

    [string]$GraphAppId,

    [string]$TeamsWebhookUrl = "",

    [string]$SharePointSiteUrl = "",

    [switch]$SkipBicep
)

$ErrorActionPreference = "Stop"

# ── Banner ──────────────────────────────────────────────────
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Intune Wipe Monitor Report — Deploy                  ║" -ForegroundColor Cyan
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

$tenantId = $account.tenantId

# ════════════════════════════════════════════════════════════
# STEP 1: App Registration
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 1: App Registration ═══" -ForegroundColor Cyan

$graphResourceId = "00000003-0000-0000-c000-000000000000"

if ($GraphAppId) {
    Write-Host "[APP-REG] Riuso app registration esistente: $GraphAppId" -ForegroundColor Yellow
    $graphObjectId = az ad app show --id $GraphAppId --query id -o tsv 2>$null
    $graphClientId = $GraphAppId
} else {
    Write-Host "[APP-REG] Creazione Intune.WipeMonitor.Report..." -ForegroundColor Yellow
    $app = az ad app create --display-name "Intune.WipeMonitor.Report" --sign-in-audience AzureADMyOrg -o json 2>$null | ConvertFrom-Json
    $graphClientId = $app.appId
    $graphObjectId = $app.id
    Write-Host "  Client ID: $graphClientId" -ForegroundColor Green
}

# Permessi necessari per il report
$permissions = @(
    @{ id = "243333ab-4d21-40cb-a475-36241daa0842"; name = "DeviceManagementManagedDevices.ReadWrite.All" }   # Wipe actions
    @{ id = "7438b122-aefc-4978-80ed-43db9fcc7571"; name = "Device.Read.All" }                                # Entra devices
    @{ id = "5ac13192-7ace-4fcf-b828-1a26f28068ee"; name = "DeviceManagementServiceConfig.ReadWrite.All" }     # Autopilot
    @{ id = "332a536c-c7ef-4017-ab91-336970924f0d"; name = "Sites.ReadWrite.All" }                             # SharePoint upload
)

foreach ($perm in $permissions) {
    az ad app permission add --id $graphObjectId --api $graphResourceId --api-permissions "$($perm.id)=Role" 2>$null
    Write-Host "  + $($perm.name)" -ForegroundColor Green
}

# Service Principal
$sp = az ad sp show --id $graphClientId -o json 2>$null | ConvertFrom-Json
if (-not $sp) {
    $sp = az ad sp create --id $graphClientId -o json 2>$null | ConvertFrom-Json
    Write-Host "  Service Principal creato" -ForegroundColor Green
}

# Admin consent
az ad app permission admin-consent --id $graphObjectId 2>$null
Write-Host "  Admin consent concesso" -ForegroundColor Green

# Client secret (solo se app nuova)
if (-not $GraphAppId) {
    $secret = az ad app credential reset --id $graphObjectId --display-name "Report-Deploy" --years 2 -o json 2>$null | ConvertFrom-Json
    $graphSecret = $secret.password
    Write-Host "  Client Secret creato" -ForegroundColor Green
} else {
    $graphSecret = Read-Host "Inserisci il Client Secret per $GraphAppId" -AsSecureString | ConvertFrom-SecureString -AsPlainText
}

# ════════════════════════════════════════════════════════════
# STEP 2: SharePoint Site/Drive ID
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 2: SharePoint Configuration ═══" -ForegroundColor Cyan

$spSiteId = ""
$spDriveId = ""

if ($SharePointSiteUrl) {
    # Estrai hostname e path dal URL
    $uri = [Uri]$SharePointSiteUrl
    $spHost = $uri.Host
    $spPath = $uri.AbsolutePath.TrimEnd('/')

    Write-Host "[SP] Recupero Site ID per $SharePointSiteUrl..." -ForegroundColor Yellow
    $siteInfo = az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/${spHost}:${spPath}" -o json 2>$null | ConvertFrom-Json
    if ($siteInfo) {
        $spSiteId = $siteInfo.id
        Write-Host "  Site ID: $spSiteId" -ForegroundColor Green

        # Recupera il drive ID della document library principale
        $drives = az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/$spSiteId/drives" -o json 2>$null | ConvertFrom-Json
        $docLib = $drives.value | Where-Object { $_.driveType -eq "documentLibrary" } | Select-Object -First 1
        if ($docLib) {
            $spDriveId = $docLib.id
            Write-Host "  Drive ID: $spDriveId ($($docLib.name))" -ForegroundColor Green
        }
    }
} else {
    Write-Host "[SP] SharePointSiteUrl non specificato." -ForegroundColor Yellow
    Write-Host "     Puoi configurarlo dopo con:" -ForegroundColor Yellow
    Write-Host '     az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/{host}:/{path}"' -ForegroundColor DarkGray
}

# ════════════════════════════════════════════════════════════
# STEP 3: Bicep Deploy
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 3: Azure Infrastructure ═══" -ForegroundColor Cyan

if (-not $SkipBicep) {
    az group create --name $ResourceGroup --location $Location -o none 2>$null
    Write-Host "[BICEP] Resource Group: $ResourceGroup" -ForegroundColor Green

    $scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
    $bicepFile = Join-Path $scriptDir "main.bicep"

    $deployment = az deployment group create `
        --resource-group $ResourceGroup `
        --template-file $bicepFile `
        --parameters location=$Location prefix=$Prefix `
            graphTenantId=$tenantId graphClientId=$graphClientId graphClientSecret=$graphSecret `
            sharePointSiteId=$spSiteId sharePointDriveId=$spDriveId `
            sharePointFolderPath="WipeMonitor Reports" `
            teamsWebhookUrl=$TeamsWebhookUrl `
            reportDays=30 `
        -o json 2>$null | ConvertFrom-Json

    $funcAppName = $deployment.properties.outputs.functionAppName.value
    Write-Host "[BICEP] Function App: $funcAppName" -ForegroundColor Green
} else {
    $funcAppName = "$Prefix-func"
    Write-Host "[BICEP] Skipped. Using: $funcAppName" -ForegroundColor Yellow
}

# ════════════════════════════════════════════════════════════
# STEP 4: Publish Function App
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "═══ STEP 4: Publish Function App ═══" -ForegroundColor Cyan

$projectDir = Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)
$publishDir = Join-Path $projectDir "publish"

Write-Host "[PUBLISH] dotnet publish..." -ForegroundColor Yellow
dotnet publish "$projectDir\Intune.WipeMonitor.Report.csproj" -c Release -o $publishDir 2>&1 | Select-Object -Last 3

$zipPath = Join-Path $projectDir "publish.zip"
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath -Force

Write-Host "[DEPLOY] Deploying to $funcAppName..." -ForegroundColor Yellow
az functionapp deployment source config-zip --resource-group $ResourceGroup --name $funcAppName --src $zipPath 2>&1 | Out-Null
Write-Host "[DEPLOY] Deployment completato!" -ForegroundColor Green

# ════════════════════════════════════════════════════════════
# Summary
# ════════════════════════════════════════════════════════════
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════════╗" -ForegroundColor Green
Write-Host "║  Deploy completato!                                   ║" -ForegroundColor Green
Write-Host "╚═══════════════════════════════════════════════════════╝" -ForegroundColor Green
Write-Host ""
Write-Host "  Function App:    $funcAppName.azurewebsites.net"
Write-Host "  Schedule:        Ogni lunedì alle 08:00 UTC"
Write-Host "  Graph App:       $graphClientId"
Write-Host "  SharePoint Site: $spSiteId"
Write-Host "  Report Days:     30"
Write-Host ""
if (-not $SharePointSiteUrl) {
    Write-Host "  ⚠ Configura SharePoint Site/Drive ID nelle App Settings" -ForegroundColor Yellow
}
if (-not $TeamsWebhookUrl) {
    Write-Host "  ⚠ Configura TeamsWebhookUrl per le notifiche" -ForegroundColor Yellow
}
