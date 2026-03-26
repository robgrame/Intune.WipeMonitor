<#
.SYNOPSIS
    Esporta tutte le Device Actions (Factory Reset/Wipe) da Microsoft Intune via Graph API REST.

.DESCRIPTION
    Recupera tutte le remoteActionAudits da Graph API usando chiamate REST pure (Invoke-RestMethod),
    autenticazione Device Code Flow (interattiva, le tue credenziali), paginazione automatica.
    Esporta in JSON e CSV. Nessun modulo Graph SDK richiesto.

.PARAMETER TenantId
    Azure AD Tenant ID.

.PARAMETER ClientId
    App Registration Client ID (deve avere DeviceManagementManagedDevices.Read.All come delegated permission).
    Per usare il well-known client ID di Microsoft Graph PowerShell: 14d82eec-204b-4c2f-b7e8-296a70dab67e

.PARAMETER OutputPath
    Cartella di output per i file JSON e CSV. Default: ./export

.PARAMETER Filter
    Filtro OData. Default: action eq 'factoryReset'.
    Usa 'all' per tutte le device actions.

.EXAMPLE
    .\Export-DeviceActions.ps1 -TenantId "d6dbad84-5922-4700-a049-c7068c37c884"

.EXAMPLE
    # Con client ID custom
    .\Export-DeviceActions.ps1 -TenantId "xxx" -ClientId "yyy"

.EXAMPLE
    # Tutte le device actions
    .\Export-DeviceActions.ps1 -TenantId "xxx" -Filter "all"
#>

[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$TenantId,

    [string]$ClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e",

    [string]$OutputPath = "./export",

    [string]$Filter = "action eq 'factoryReset'"
)

$ErrorActionPreference = "Stop"

# ── Banner ──────────────────────────────────────────────────
Write-Host ""
Write-Host "╔═══════════════════════════════════════════════════╗" -ForegroundColor Cyan
Write-Host "║  Intune Device Actions Export                     ║" -ForegroundColor Cyan
Write-Host "║  REST API + Device Code Auth → JSON + CSV         ║" -ForegroundColor Cyan
Write-Host "╚═══════════════════════════════════════════════════╝" -ForegroundColor Cyan
Write-Host ""

# ── Device Code Flow ────────────────────────────────────────
Write-Host "[AUTH] Richiesta Device Code..." -ForegroundColor Yellow

$deviceCodeBody = @{
    client_id = $ClientId
    scope     = "https://graph.microsoft.com/DeviceManagementManagedDevices.Read.All https://graph.microsoft.com/DeviceManagementRBAC.Read.All offline_access"
}
$deviceCodeResponse = Invoke-RestMethod `
    -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/devicecode" `
    -Method Post -Body $deviceCodeBody

Write-Host ""
Write-Host "  ┌──────────────────────────────────────────────┐" -ForegroundColor Green
Write-Host "  │  $($deviceCodeResponse.message)" -ForegroundColor Green
Write-Host "  └──────────────────────────────────────────────┘" -ForegroundColor Green
Write-Host ""
Write-Host "[AUTH] In attesa dell'autenticazione nel browser..." -ForegroundColor Yellow

# Poll per il token
$tokenBody = @{
    grant_type  = "urn:ietf:params:oauth:grant-type:device_code"
    client_id   = $ClientId
    device_code = $deviceCodeResponse.device_code
}
$token = $null
$pollInterval = [Math]::Max($deviceCodeResponse.interval, 5)

while (-not $token) {
    Start-Sleep -Seconds $pollInterval
    try {
        $tokenResponse = Invoke-RestMethod `
            -Uri "https://login.microsoftonline.com/$TenantId/oauth2/v2.0/token" `
            -Method Post -Body $tokenBody
        $token = $tokenResponse.access_token
    }
    catch {
        $errBody = $_.ErrorDetails.Message | ConvertFrom-Json -ErrorAction SilentlyContinue
        if ($errBody.error -eq "authorization_pending") {
            Write-Host "." -NoNewline -ForegroundColor DarkGray
            continue
        }
        elseif ($errBody.error -eq "slow_down") {
            $pollInterval += 5
            continue
        }
        else {
            Write-Error "Autenticazione fallita: $($errBody.error_description ?? $_.Exception.Message)"
        }
    }
}

Write-Host ""
Write-Host "[AUTH] Autenticato!" -ForegroundColor Green

# ── Recupero dati con paginazione ───────────────────────────
$baseUrl = "https://graph.microsoft.com/beta/deviceManagement/remoteActionAudits"
if ($Filter -eq "all") {
    $url = "${baseUrl}?`$orderby=requestDateTime desc&`$top=100"
}
else {
    $url = "${baseUrl}?`$filter=$Filter&`$orderby=requestDateTime desc&`$top=100"
}

$headers = @{
    Authorization  = "Bearer $token"
    "Content-Type" = "application/json"
}

$allActions = [System.Collections.Generic.List[object]]::new()
$page = 1

Write-Host ""
Write-Host "[FETCH] Recupero device actions..." -ForegroundColor Yellow

while ($url) {
    $response = Invoke-RestMethod -Uri $url -Headers $headers -Method Get
    $count = ($response.value | Measure-Object).Count

    if ($count -gt 0) {
        foreach ($item in $response.value) { $allActions.Add($item) }
        Write-Host "  Pagina $page : $count record (totale: $($allActions.Count))" -ForegroundColor DarkGray
    }

    $url = $response.'@odata.nextLink'
    $page++
}

Write-Host "[FETCH] Completato: $($allActions.Count) device actions" -ForegroundColor Green

if ($allActions.Count -eq 0) {
    Write-Host "[!] Nessuna device action trovata." -ForegroundColor Yellow
    exit 0
}

# ── Statistiche ─────────────────────────────────────────────
Write-Host ""
Write-Host "── Riepilogo ──────────────────────────────────────" -ForegroundColor Cyan

$stats = $allActions | Group-Object -Property actionState
foreach ($s in $stats | Sort-Object Name) {
    $icon = switch ($s.Name) {
        "done"    { "✅" }
        "pending" { "⏳" }
        "failed"  { "❌" }
        default   { "  " }
    }
    Write-Host "  $icon $($s.Name): $($s.Count)"
}
Write-Host "  ── TOTALE: $($allActions.Count)"

$devices = $allActions | Select-Object -Property deviceDisplayName -Unique
Write-Host "  🖥️  Device unici: $($devices.Count)"
Write-Host ""

# ── Export ──────────────────────────────────────────────────
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

$timestamp = Get-Date -Format "yyyyMMdd_HHmmss"
$jsonFile = Join-Path $OutputPath "device-actions_$timestamp.json"
$csvFile  = Join-Path $OutputPath "device-actions_$timestamp.csv"

# JSON
$exportPayload = [ordered]@{
    exportedAt   = (Get-Date -Format "o")
    tenant       = $TenantId
    filter       = $Filter
    totalRecords = $allActions.Count
    statistics   = [ordered]@{}
    actions      = $allActions
}
foreach ($s in $stats | Sort-Object Name) {
    $exportPayload.statistics[$s.Name] = $s.Count
}
$exportPayload | ConvertTo-Json -Depth 10 | Out-File -Encoding utf8 -FilePath $jsonFile

$jsonSize = [math]::Round((Get-Item $jsonFile).Length / 1KB, 1)
Write-Host "[EXPORT] JSON: $jsonFile ($jsonSize KB)" -ForegroundColor Green

# CSV
$allActions | ForEach-Object {
    [PSCustomObject]@{
        Id                         = $_.id
        DeviceName                 = $_.deviceDisplayName
        Action                     = $_.action
        ActionState                = $_.actionState
        RequestDateTime            = $_.requestDateTime
        UserName                   = $_.userName
        InitiatedBy                = $_.initiatedByUserPrincipalName
        DeviceOwner                = $_.deviceOwnerUserPrincipalName
        ManagedDeviceId            = $_.managedDeviceId
        DeviceIMEI                 = $_.deviceIMEI
        DeviceActionCategory       = $_.deviceActionCategory
        BulkDeviceActionId         = $_.bulkDeviceActionId
    }
} | Export-Csv -Path $csvFile -NoTypeInformation -Encoding utf8

$csvSize = [math]::Round((Get-Item $csvFile).Length / 1KB, 1)
Write-Host "[EXPORT] CSV:  $csvFile ($csvSize KB)" -ForegroundColor Green

Write-Host ""
Write-Host "Done! ✅" -ForegroundColor Green
