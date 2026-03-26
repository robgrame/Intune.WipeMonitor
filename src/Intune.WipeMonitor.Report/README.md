# Intune Wipe Monitor — Report (Azure Function)

Soluzione standalone che genera periodicamente un report Excel delle azioni di wipe Intune, cross-referenziando con Entra ID per identificare i device pronti per la cancellazione.

## Architettura

```
┌─────────────────────────────────────────────────────────┐
│  Azure Function (Timer Trigger — ogni lunedì 08:00 UTC) │
│                                                         │
│  ① Query Graph API: remoteActionAudits (factoryReset)   │
│  ② Per ogni device: verifica presenza in Entra ID       │
│  ③ Genera Excel (.xlsx) con ClosedXML                   │
│  ④ Upload su SharePoint Document Library                │
│  ⑤ Post Adaptive Card su Teams con link al report       │
└─────────────────────────────────────────────────────────┘
```

## Report Excel — 3 Sheets

| Sheet | Contenuto |
|---|---|
| **Wipe Actions (30d)** | Tutte le azioni di wipe dell'ultimo mese con stato Entra |
| **Entra Cleanup Pending** | Device wipati con oggetto Entra ancora presente → da cancellare |
| **Summary** | Statistiche: totale wipe, completati, pending Entra, ecc. |

### Colonne principali

- Device Name, Wipe Requested/Completed, Action State, Initiated By
- **Entra Present** (Yes/No), OS, Trust Type, Last Entra Sign-In, Account Enabled

## Quick Start

### Prerequisites

- Azure CLI (`az login`)
- .NET 10 SDK
- Permessi per creare App Registration e risorse Azure

### 1. Deploy

```powershell
cd src/Intune.WipeMonitor.Report/Deploy

# Deploy completo (nuova app registration + infrastruttura + publish)
.\Deploy-Report.ps1 -Location westus2 -Prefix wipereport `
    -SharePointSiteUrl "https://contoso.sharepoint.com/sites/IT" `
    -TeamsWebhookUrl "https://prod-XX.westus.logic.azure.com/workflows/..."

# Oppure riusa l'app registration esistente di Intune.WipeMonitor
.\Deploy-Report.ps1 -Location westus2 -Prefix wipereport `
    -GraphAppId "f3baa699-2f8c-46e0-8e99-925fddb69030" `
    -SharePointSiteUrl "https://contoso.sharepoint.com/sites/IT"
```

### 2. Configurazione manuale (se necessario)

#### Trovare SharePoint Site ID e Drive ID

```powershell
# Site ID
az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/contoso.sharepoint.com:/sites/IT"
# → id = "contoso.sharepoint.com,guid1,guid2"

# Drive ID (document library)
az rest --method GET --url "https://graph.microsoft.com/v1.0/sites/{siteId}/drives"
# → value[0].id = "b!xxxxx"
```

#### App Settings della Function App

| Setting | Descrizione |
|---|---|
| `Graph__TenantId` | Tenant ID Entra |
| `Graph__ClientId` | Client ID app registration |
| `Graph__ClientSecret` | Client secret |
| `Report__SharePointSiteId` | ID del sito SharePoint |
| `Report__SharePointDriveId` | ID del drive (document library) |
| `Report__SharePointFolderPath` | Cartella per i report (default: `WipeMonitor Reports`) |
| `Report__TeamsWebhookUrl` | URL webhook Power Automate per Teams |
| `Report__ReportDays` | Giorni da includere nel report (default: `30`) |
| `Report__Schedule` | CRON expression per la frequenza (default: `0 0 8 * * 1` = lunedì 08:00 UTC) |

### 3. Teams Webhook (Power Automate)

1. In Teams, vai al canale → **Workflows** → **Post to a channel when a webhook request is received**
2. Copia l'URL del webhook
3. Configuralo in `Report__TeamsWebhookUrl`

## Graph API Permissions

| Permission | Type | Scopo |
|---|---|---|
| `DeviceManagementManagedDevices.ReadWrite.All` | Application | Leggere wipe actions |
| `DeviceManagementServiceConfig.ReadWrite.All` | Application | Autopilot device info |
| `Device.Read.All` | Application | Leggere device Entra ID |
| `Sites.ReadWrite.All` | Application | Upload report su SharePoint |

> **Nota**: Se riusi la stessa app registration di Intune.WipeMonitor, aggiungi solo `Device.Read.All` e `Sites.ReadWrite.All`.

## Struttura progetto

```
src/Intune.WipeMonitor.Report/
├── Deploy/
│   ├── main.bicep                 # Infrastruttura Azure (Function App, Storage, App Insights)
│   ├── main.parameters.json       # Template parametri
│   └── Deploy-Report.ps1          # Script end-to-end (app reg + bicep + publish)
├── Functions/
│   └── WipeReportFunction.cs      # Timer trigger (lunedì 08:00 UTC)
├── Models/
│   ├── ReportSettings.cs          # Configurazione report + Graph
│   └── WipeReportEntry.cs         # Modello dati report + DTO Graph
├── Services/
│   ├── GraphReportService.cs      # Fetch wipe actions + Entra lookup
│   ├── ExcelReportBuilder.cs      # Generazione Excel con ClosedXML
│   ├── SharePointUploadService.cs # Upload su SharePoint via Graph
│   └── TeamsReportNotifier.cs     # Adaptive Card su Teams
├── Program.cs                     # Host + DI
├── host.json                      # Azure Functions config
└── local.settings.json            # Sviluppo locale
```

## Sviluppo locale

```powershell
cd src/Intune.WipeMonitor.Report

# Configura local.settings.json con le credenziali Graph e SharePoint

# Avvia
func start

# Oppure trigger manuale
func start --functions WipeReport
```
