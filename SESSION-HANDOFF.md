# Intune Wipe Monitor — Session Handoff

> Documento per riprendere la sessione su un'altra macchina con GitHub Copilot CLI.
> Generato il: 2026-03-25T15:13 UTC

---

## Stato attuale del progetto

La **web app Blazor** è deployata e funzionante su Azure. Il **polling Graph API** recupera correttamente le azioni di wipe da Intune. Manca il **deploy dell'agent on-prem** (Windows Service) che si connette al SignalR Hub per eseguire il cleanup su AD e SCCM.

### ✅ Completato

| Componente | Stato |
|---|---|
| Web App Blazor (Dashboard, Approvazioni, Storico) | ✅ Deployed e funzionante |
| Graph API Polling (remoteActionAudits/factoryReset) | ✅ 10 wipe actions recuperate |
| SignalR Hub (`/hub/cleanup`) | ✅ Endpoint attivo, pronto per agent |
| Azure SQL Server (Managed Identity, private endpoint) | ✅ Schema creato |
| Key Vault (Graph client secret, private endpoint) | ✅ |
| App Configuration (private endpoint, KV references) | ✅ |
| Application Insights (custom events) | ✅ |
| VNet + Private Endpoints (KV, AppConfig, SQL) | ✅ |
| Serilog + CMTrace formatter | ✅ |
| Startup banner con versione e config | ✅ |
| App Registration con Graph permissions | ✅ |
| README con badges, push su GitHub | ✅ |

### ❌ Da completare

| Componente | Descrizione |
|---|---|
| **Agent On-Prem** | Compilare e deployare `Intune.WipeMonitor.Agent` come Windows Service su una VM on-prem |
| **Configurare Agent** | Impostare `appsettings.json` con connessione AD, SCCM, SignalR Hub URL |
| **Test E2E** | Approvare un cleanup dalla dashboard e verificare che l'agent elimini da AD/SCCM |
| **Serilog Agent** | Verificare che i log CMTrace funzionino anche sull'agent |

---

## Architettura

```
┌─────────────────────────── AZURE ───────────────────────────┐
│  Web App (Blazor Server)                                    │
│  ├── Dashboard UI (https://intune-wipemonitor-app...)        │
│  ├── Graph API Polling (ogni 60 min)                        │
│  ├── SignalR Hub (/hub/cleanup)  ◄──── WSS ────┐            │
│  ├── Azure SQL (wipemonitor-sql, private EP)    │            │
│  ├── Key Vault (wipemonitor-kv, private EP)     │            │
│  ├── App Config (wipemonitor-appconfig, priv EP)│            │
│  └── App Insights (wipemonitor-insights)        │            │
└─────────────────────────────────────────────────┼────────────┘
                                                  │
┌─────────────────── ON-PREMISES ─────────────────┼────────────┐
│  Agent (Windows Service)                        │            │
│  ├── SignalR Client ────────────────────────────┘            │
│  ├── AD Cleanup (LDAP, System.DirectoryServices.Protocols)   │
│  ├── SCCM Cleanup (AdminService REST API)                    │
│  └── App Insights (stessi custom events)                     │
└──────────────────────────────────────────────────────────────┘
```

---

## Risorse Azure (IntuneWipeMonitor-RG, westus2)

| Risorsa | Nome | Note |
|---|---|---|
| App Service Plan | `wipemonitor-plan` | B1 Linux |
| Web App | `intune-wipemonitor-app` | .NET 10, Managed Identity |
| SQL Server | `wipemonitor-sql` | Entra-only auth, private EP |
| SQL Database | `wipemonitor-db` | Basic 5 DTU |
| Key Vault | `wipemonitor-kv` | RBAC, private EP |
| App Configuration | `wipemonitor-appconfig` | Standard, private EP |
| Application Insights | `wipemonitor-insights` | Conn string sotto |
| VNet | `wipemonitor-vnet` | 10.0.0.0/16 |
| Subnet webapp | `snet-webapp` | 10.0.1.0/24 (delegated) |
| Subnet PE | `snet-privateendpoints` | 10.0.2.0/24 |

### App Registration

| Campo | Valore |
|---|---|
| Nome | `Intune.WipeMonitor` |
| Client ID | `f3baa699-2f8c-46e0-8e99-925fddb69030` |
| Object ID (app) | `50b1ce5d-541e-4b49-9329-a66df031d552` |
| SP Object ID | `8ee1113e-8471-436d-bc5f-52ecac5c12f3` |
| Tenant ID | `d6dbad84-5922-4700-a049-c7068c37c884` |
| Subscription | `74c8c33a-f447-4f1e-890c-f6f71833c8be` |
| Permessi | `DeviceManagementManagedDevices.ReadWrite.All`, `DeviceManagementRBAC.Read.All` (Application) |

### Application Insights Connection String

```
InstrumentationKey=38846ce6-422a-4112-b3b0-b7be7b1caece;IngestionEndpoint=https://westus2-2.in.applicationinsights.azure.com/;LiveEndpoint=https://westus2.livediagnostics.monitor.azure.com/;ApplicationId=5e062894-bda8-460b-aec6-666143e072f3
```

---

## GitHub Repo

- **URL**: https://github.com/robgrame/Intune.WipeMonitor
- **Branch**: `master`

### Struttura progetto

```
src/
├── Intune.WipeMonitor/            # Web App (Blazor Server) — DEPLOYED
│   ├── Program.cs                 # Startup: Serilog, App Config, EF Core, SignalR
│   ├── Hubs/CleanupHub.cs         # SignalR Hub (gateway verso agent)
│   ├── Services/
│   │   ├── GraphWipeMonitorService.cs   # Polling Graph API
│   │   ├── CleanupOrchestrator.cs       # Orchestrazione cleanup via SignalR
│   │   ├── CleanupTelemetryService.cs   # Custom events App Insights
│   │   └── WipeMonitorBackgroundService.cs
│   ├── Components/Pages/          # Blazor: Dashboard, Approvals, History
│   ├── Data/AppDbContext.cs        # EF Core (SQL Server)
│   └── Models/                     # DeviceCleanupRecord, Settings, WipeAction
│
├── Intune.WipeMonitor.Agent/      # Agent On-Prem — DA DEPLOYARE
│   ├── Program.cs                 # Startup: Serilog, App Insights, SignalR client
│   ├── CleanupAgentWorker.cs      # Worker: connessione Hub, handler AD/SCCM
│   ├── AgentSettings.cs           # Config: Hub URL, AD, SCCM
│   ├── Services/
│   │   ├── ActiveDirectoryService.cs  # LDAP delete computer
│   │   └── SccmService.cs            # AdminService REST delete
│   └── appsettings.json           # Template configurazione
│
└── Intune.WipeMonitor.Shared/     # Modelli condivisi
    ├── Enums.cs                   # CleanupStatus, StepResult, CleanupTarget
    ├── HubContracts.cs            # ICleanupAgentClient, ICleanupHub, CleanupCommand
    ├── TelemetryEvents.cs         # Nomi eventi App Insights
    └── Logging/
        ├── CMTraceFormatter.cs    # Serilog formatter stile SCCM
        └── StartupBanner.cs       # Banner ASCII + config dump
```

---

## Prossimi step: Deploy Agent On-Prem

### 1. Clona il repo sulla VM

```powershell
git clone https://github.com/robgrame/Intune.WipeMonitor.git
cd Intune.WipeMonitor
```

### 2. Configura `appsettings.json` dell'agent

File: `src/Intune.WipeMonitor.Agent/appsettings.json`

```json
{
  "ApplicationInsights": {
    "ConnectionString": "InstrumentationKey=38846ce6-422a-4112-b3b0-b7be7b1caece;IngestionEndpoint=https://westus2-2.in.applicationinsights.azure.com/;LiveEndpoint=https://westus2.livediagnostics.monitor.azure.com/;ApplicationId=5e062894-bda8-460b-aec6-666143e072f3"
  },
  "Agent": {
    "AgentId": "AGENT-01",
    "HubUrl": "https://intune-wipemonitor-app.azurewebsites.net/hub/cleanup",
    "HeartbeatIntervalSeconds": 60,
    "ActiveDirectory": {
      "Server": "<DC_HOSTNAME>",
      "SearchBase": "<DC=domain,DC=com>",
      "Port": 389
    },
    "Sccm": {
      "AdminServiceUrl": "https://<SCCM_SERVER>/AdminService"
    }
  }
}
```

### 3. Pubblica e installa come Windows Service

```powershell
cd src\Intune.WipeMonitor.Agent
dotnet publish -c Release -o C:\Services\WipeMonitor.Agent

# Installa come Windows Service
sc.exe create "Intune.WipeMonitor.Agent" binpath="C:\Services\WipeMonitor.Agent\Intune.WipeMonitor.Agent.exe"
sc.exe config "Intune.WipeMonitor.Agent" start=auto
sc.exe start "Intune.WipeMonitor.Agent"
```

### 4. Verifica

- Nella dashboard Blazor, sezione "Agent On-Premises" deve mostrare l'agent come **Online**
- Nei log CMTrace: `logs/wipemonitor-agent-YYYYMMDD.log`
- In App Insights: evento `Agent.Connected`

### 5. Test E2E

1. Dalla dashboard, vai su **Approvazioni**
2. Seleziona un device con wipe "done"
3. Clicca **Approva Cleanup**
4. L'orchestratore invia il comando all'agent via SignalR
5. L'agent esegue:
   - `RemoveFromActiveDirectory` → LDAP delete
   - `RemoveFromSccm` → AdminService DELETE
6. Il risultato torna al Hub e aggiorna il DB
7. App Insights mostra `DeviceCleanup.ADDeletion` e `DeviceCleanup.SCCMDeletion`

---

## Protocollo SignalR (Hub ↔ Agent)

### Hub → Agent (server invoca metodi sul client)

| Metodo | Parametro | Ritorno | Quando |
|---|---|---|---|
| `RemoveFromActiveDirectory` | `CleanupCommand` | `CleanupStepResult` | Cleanup approvato |
| `RemoveFromSccm` | `CleanupCommand` | `CleanupStepResult` | Cleanup approvato |
| `Ping` | — | `AgentStatus` | Health check |

### Agent → Hub (client invoca metodi sul server)

| Metodo | Parametri | Quando |
|---|---|---|
| `RegisterAgent` | `AgentRegistration` | Connessione iniziale |
| `Heartbeat` | `agentId` | Ogni 60s |
| `ReportStepCompleted` | `wipeActionId, target, result` | Dopo ogni step |

---

## Custom Events App Insights (KQL)

```kql
// Tutti gli eventi di cleanup nelle ultime 24h
customEvents
| where name startswith "DeviceCleanup"
| where timestamp > ago(24h)
| project timestamp, name,
    DeviceName = tostring(customDimensions.DeviceName),
    Result = tostring(customDimensions.Result),
    Error = tostring(customDimensions.ErrorMessage)
| order by timestamp desc

// Stato agent
customEvents
| where name startswith "Agent."
| project timestamp, name,
    AgentId = tostring(customDimensions.AgentId),
    Machine = tostring(customDimensions.AgentMachine)
| order by timestamp desc
```

---

## Note tecniche

- Il web app **non** ha configurazione AD/SCCM — queste sono solo nell'agent
- L'agent si connette in **uscita** (WSS) al Hub — nessuna regola firewall inbound necessaria
- Il secret Graph è in Key Vault (`Graph--ClientSecret`) e anche come App Setting (`Graph__ClientSecret`) come fallback
- L'App Config SDK ha un timeout di 20s allo startup con fallback graceful
- Il DB schema viene creato in background (non blocca lo startup)
- Serilog usa `builder.Host.UseSerilog((context, configuration) => ...)` — senza callback `services` per evitare crash allo startup
