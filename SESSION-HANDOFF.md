# Intune Wipe Monitor — Session Handoff v2

> Documento per riprendere la sessione su un'altra macchina con GitHub Copilot CLI.
> Generato il: 2026-03-26T10:11 UTC

---

## Stato attuale — Cosa funziona

| Componente | Stato | Note |
|---|---|---|
| **Web App Blazor** | ✅ Live | https://intune-wipemonitor-app.azurewebsites.net |
| **Entra ID Auth** | ✅ | Ruolo WipeMonitor-Admin richiesto |
| **Graph API Polling** | ✅ | 10 wipe actions recuperate ogni 60 min |
| **Autopilot Cleanup** | ✅ | Primo step cleanup via Graph API |
| **SignalR Hub** | ✅ | `/hub/cleanup` con API key auth |
| **Teams Notifications** | ✅ | Adaptive Card via Power Automate webhook |
| **Azure SQL → SQLite** | ✅ | `/home/wipemonitor.db` persistente |
| **Key Vault** | ✅ | Private endpoint, Graph secret |
| **App Configuration** | ✅ | Private endpoint, tutte le config |
| **App Insights** | ✅ | Custom events per audit |
| **Serilog + CMTrace** | ✅ | File log format CMTrace |
| **IaC Bicep** | ✅ | `Deploy/main.bicep` + `Deploy-Infrastructure.ps1` |
| **Security hardening** | ✅ | HTTPS-only, TLS 1.2, LDAP/OData injection fixes |

## ❌ Azione richiesta ORA: Aggiornare l'Agent on-prem

L'agent ha un errore di negoziazione SignalR perché il server ora richiede API key auth. L'agent va ricompilato e rideployato sulla VM.

### Passi:

```powershell
# 1. Pull the latest code
cd C:\path\to\Intune.WipeMonitor
git pull

# 2. Edit appsettings.json — add ApiKey to Agent section
# File: src/Intune.WipeMonitor.Agent/appsettings.json
```

Aggiungere `ApiKey` nella sezione Agent:
```json
{
  "Agent": {
    "AgentId": "YOURNAME",
    "HubUrl": "https://intune-wipemonitor-app.azurewebsites.net/hub/cleanup",
    "ApiKey": "1wpaDNk3u1dwzIfu3SKIomilG+50LZm7MozTALRnlYk=",
    "HeartbeatIntervalSeconds": 60,
    "ActiveDirectory": { ... },
    "Sccm": { ... }
  }
}
```

```powershell
# 3. Publish
cd src\Intune.WipeMonitor.Agent
dotnet publish -c Release -o C:\Services\WipeMonitor.Agent

# 4. Restart service
sc.exe stop "Intune.WipeMonitor.Agent"
sc.exe start "Intune.WipeMonitor.Agent"

# 5. Verify in logs: should see "Connesso al Hub" without negotiation errors
```

### Dopo il restart, verificare:
- Nei log CMTrace dell'agent: `Agent registrato al Hub come YOURNAME`
- Nella dashboard web: pagina **Stato Servizi** mostra LED verde per Gateway
- Nella pagina **Dashboard**: barra infrastruttura mostra Gateway/AD/SCCM con LED

---

## Architettura

```
CLEANUP PIPELINE (4 step in sequenza dopo approvazione):

① Autopilot   → Graph API DELETE windowsAutopilotDeviceIdentities (cloud)
② AD          → SignalR → Agent → LDAP Delete computer object (on-prem)
③ SCCM        → SignalR → Agent → AdminService REST DELETE (on-prem)
④ Intune       → Graph API DELETE managedDevices (cloud)
   Entra ID   → Automatico dopo AD sync
```

## Risorse Azure (IntuneWipeMonitor-RG, westus2)

| Risorsa | Nome | Note |
|---|---|---|
| App Service | `intune-wipemonitor-app` | .NET 10, Managed Identity, HTTPS-only, TLS 1.2 |
| Key Vault | `wipemonitor-kv` | Private endpoint, RBAC |
| App Configuration | `wipemonitor-appconfig` | Private endpoint |
| Application Insights | `wipemonitor-insights` | Custom events |
| VNet | `wipemonitor-vnet` | 10.0.0.0/16 |

### App Registrations

| App | Client ID | Scopo |
|---|---|---|
| `Intune.WipeMonitor` | `f3baa699-2f8c-46e0-8e99-925fddb69030` | Graph API (wipe polling, Autopilot/Intune delete) |
| `Intune.WipeMonitor.Portal` | `42c85dd0-f652-459f-bf41-caf7ec741c00` | Dashboard auth (Entra ID + WipeMonitor-Admin role) |

### Tenant & Subscription
- Tenant: `d6dbad84-5922-4700-a049-c7068c37c884`
- Subscription: `74c8c33a-f447-4f1e-890c-f6f71833c8be`

## SignalR Auth

Il Hub usa una policy `AgentOrUser` che accetta:
- **OpenIdConnect** (Entra ID) — per browser/dashboard
- **AgentApiKey** — per l'agent on-prem, via header `X-Api-Key` o query `?api_key=`

API Key corrente: `1wpaDNk3u1dwzIfu3SKIomilG+50LZm7MozTALRnlYk=`
Configurata in: `WipeMonitor:AgentApiKey` (App Settings sul Web App)

## Teams Notifications

- **Canale**: Device Lifecycle (Team ID: `92eca928-359a-4832-8084-cfb13daa43ce`)
- **Metodo**: Power Automate Workflows webhook (Adaptive Card)
- **Webhook URL**: configurato in `WipeMonitor__TeamsWebhookUrl` app setting
- **Test OK**: notifica arrivata nel canale ✅
- **Eventi**: approvazione richiesta + risultato cleanup

## Struttura progetto

```
Deploy/
├── main.bicep                    # IaC infrastruttura Azure
├── main.parameters.json          # Parametri Bicep
├── Deploy-Infrastructure.ps1     # Provisioning end-to-end
└── Export-DeviceActions.ps1      # Export wipe actions (Device Code Flow)

src/
├── Intune.WipeMonitor/           # Web App (Blazor Server) — DEPLOYED
│   ├── Auth/AgentApiKeyAuthHandler.cs  # API key auth per agent
│   ├── Hubs/CleanupHub.cs        # SignalR Hub
│   ├── Services/
│   │   ├── GraphWipeMonitorService.cs  # Polling + Autopilot/Intune delete
│   │   ├── CleanupOrchestrator.cs      # Pipeline 4 step
│   │   ├── CleanupTelemetryService.cs  # App Insights events
│   │   ├── TeamsNotificationService.cs # Adaptive Card webhook
│   │   └── WipeMonitorBackgroundService.cs
│   └── Components/Pages/         # Dashboard, Approvals, History, Status
│
├── Intune.WipeMonitor.Agent/     # Agent On-Prem — DA RIDEPLOYARE
│   ├── CleanupAgentWorker.cs     # SignalR client + X-Api-Key header
│   ├── AgentSettings.cs          # Config con ApiKey
│   └── Services/                 # AD (LDAP) + SCCM (AdminService)
│
└── Intune.WipeMonitor.Shared/    # Modelli, contratti, CMTrace formatter
```

## Problemi risolti in questa sessione

1. **Serilog crash startup** → Usare `(context, configuration)` callback senza `services`
2. **SQLite DateTimeOffset ORDER BY** → Migrato a Azure SQL → poi tornato a SQLite con converter
3. **App Config private endpoint DNS** → `WEBSITE_DNS_SERVER=168.63.129.16`
4. **SignalR typed hub non supporta client results** → `IHubContext<CleanupHub>` con `InvokeAsync<T>`
5. **SignalR AllowAnonymous** → Sostituito con API key auth `AgentOrUser` policy
6. **OData injection SCCM/Autopilot** → `Uri.EscapeDataString` + `Guid.TryParse`
7. **Graph 401** → Service Principal mancante + wrong permission IDs
8. **ChannelMessage.Send non esiste come Application** → Webhook approach

## Note per la prossima sessione

- Dopo aver aggiornato l'agent, testare un ciclo completo: approvazione → cleanup → Teams notification
- Considerare auto-update dell'agent (endpoint `/api/agent/update` che serve lo zip)
- Il SESSION-HANDOFF.md nella root del repo è la v1 — questa è la v2 aggiornata

